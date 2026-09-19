# DiskEye 架构文档

> 适用版本：v0.9.15
> 本文面向准备阅读或修改代码的开发者，描述的是**当前代码的实际行为**，不是最初的设计意图。

## 1. 这个工具要解决什么

Windows 上"磁盘灯一直亮"是个常见但很难回答的问题：任务管理器只能看到"系统"进程在写，看不到**具体是哪个进程写了哪个文件**。

DiskEye 用两条数据源交叉回答这个问题，这也是整个架构的核心张力：

| 数据源 | 能拿到什么 | 拿不到什么 |
|--------|-----------|-----------|
| `FileSystemWatcher` | 文件路径、操作类型、即时性 | 进程身份、写入字节数 |
| ETW Kernel-File Provider | 精确的进程 ↔ 文件对应关系、真实字节数 | 需要管理员权限，且在会话启动前已存在的进程无法回溯 |

**所有架构复杂度基本都来自这两条链路的缝合与降级。**

## 2. 进程模型

### 2.1 单 exe 双角色

`DiskEye.exe` 一个可执行文件扮演两种角色，由命令行参数分流（`Program.cs`）：

```
DiskEye.exe                      → 主进程（WinForms UI）
DiskEye.exe --etw-child <父PID>  → ETW 采集子进程（无 UI）
```

分流发生在 `Main` 的最开始，**早于 Mutex 创建和 WinForms 初始化**——子进程绝不触碰任何 UI 代码。

为什么要拆子进程：ETW 会话的启动过程在某些机器上会阻塞调用线程，早期版本直接在主进程起会话导致启动卡死。放进子进程后，主进程不再被 ETW 初始化拖住，父子之间用 NamedPipe + 自定义帧协议（`Etw/FrameProtocol.cs`）通信。

### 2.2 单实例与唤醒

- 互斥量：`Global\DiskEye.SingleInstance.v1`
- 唤醒管道：NamedPipe `DiskEye.Pipe.v1`

第二个实例启动时，通过管道给第一个实例发 `SHOW` 后自行退出。Mutex 用 `initiallyOwned: true` 创建并捕获 `AbandonedMutexException`——上一个进程异常退出留下孤儿 Mutex 时，能自动恢复而不是永久拒绝启动。

## 3. 组件职责

`MonitorService` 是唯一的总控，负责按配置装配、启停所有组件。

| 组件 | 文件 | 职责 |
|------|------|------|
| `MonitorService` | `Monitoring/MonitorService.cs` | 总控。装配组件、按配置增减盘符、启动时执行一次归档 |
| `FileSystemWatcherMonitor` | `Monitoring/FileSystemWatcherMonitor.cs` | **每盘一个实例**，捕获文件事件，投递进聚合器队列 |
| `EventAggregator` | `Monitoring/EventAggregator.cs` | 消费事件队列，做聚合、按盘符过滤、突增检测 |
| `AttributionEngine` | `Monitoring/AttributionEngine.cs` | 把文件事件归因到进程（v1/v2 两套算法，由配置切换） |
| `EtwProcessResolver` | `Monitoring/EtwProcessResolver.cs` | 主进程侧的 ETW 会话管理与 PID→进程名解析 |
| `EtwChildWorker` | `Etw/EtwChildWorker.cs` | 子进程侧的 ETW 事件采集与内核文件事件解析 |
| `EtwFrameClient` | `Monitoring/EtwFrameClient.cs` | 主进程侧读取子进程帧的客户端 |
| `EventStore` | `Storage/EventStore.cs` | SQLite 读写、建表、归档、CSV 导出 |
| `V1AttributionShadow` | `Monitoring/V1AttributionShadow.cs` | A/B 影子对照：并行跑 v1 算法，只记统计不落地事件 |

### 启动顺序（有讲究）

`MonitorService.Start()` 里的顺序不是随意排的：

1. `Store` → `Etw` → `Engine` / `Aggregator` — 先让"能独立工作的部分"跑起来
2. `Etw.Start()` → `Aggregator.Start()`
3. `SyncAllowedDrives()` — 同步盘符过滤
4. `FrameClient?.Start()` — **ETW 子进程最后启动**

原因是：子进程就绪前，主进程已经能用 `FileSystemWatcher` + 启发式归因正常工作。子进程就绪帧到达后，直接喂给已在运转的引擎。这样即使 ETW 全程不可用，工具也只是一个"精度较低"的工具，而不是一个"打不开"的工具。

## 4. 数据流

```
                    ┌──────────────────────────┐
  FileSystemWatcher │ 文件事件（路径/操作类型） │
   (每盘一个实例)    └────────────┬─────────────┘
                                 │ Enqueue
                                 ▼
  ETW 子进程 ──NamedPipe 帧──▶ EtwFrameClient
                                 │
                                 ▼
                         ┌───────────────┐
                         │ EventAggregator│ ← 盘符过滤、突增检测
                         └───────┬───────┘
                                 │
                                 ▼
                         ┌───────────────┐
                         │AttributionEngine│ ← PID → 进程名/路径
                         └───────┬───────┘
                                 │
                                 ▼
                         ┌───────────────┐        ┌──────────────┐
                         │   EventStore   │───────▶│  MainForm UI │
                         │   (SQLite)     │  查询  │  (3s 刷新)   │
                         └───────────────┘        └──────────────┘
```

聚合器的写入是**异步批量**的，`FileSystemWatcher` 回调里只做入队，避免在文件系统回调线程上做数据库 IO。

## 5. 存储设计

数据库文件：exe 同目录 `events.db`，SQLite WAL 模式。

### 表结构

| 表 | 用途 | 关键列 |
|----|------|--------|
| `events` | 文件事件流水 | `ts`, `drive`, `folder`, `process_name`, `process_path` |
| `write_bytes` | **真实写入字节**（权威口径） | `ts`, `pid`, `process_name`, `folder`, `bytes` |
| `attribution_ab_hourly` | v1/v2 归因算法每小时对照 | `hour`, `fsw_events`, `v1_named`, `v2_named` |
| `events_archive` | 超过保留天数的归档事件 | 同 `events` + `archived_at` |

**注意 `events` 和 `write_bytes` 的分工**：榜单排名和"今日写入量"一律以 `write_bytes` 为准（ETW FileIo/Write 每秒聚合的真实字节），`events` 主要用于事件流展示。想统计写入量却去查 `events` 表，会得到错误数字。

### 索引

```
events:       ix_events_ts(ts), ix_events_process(process_name), ix_events_folder(folder)
write_bytes:  ix_wb_ts(ts), ix_wb_proc(process_name, ts), ix_wb_folder(folder, ts)
events_archive: ix_archive_ts(ts)
```

时间列和聚合维度都建了索引，按时间范围的查询可以直接走索引。

### PRAGMA 设置及原因

```sql
PRAGMA journal_mode = WAL;          -- 读写并发
PRAGMA synchronous = NORMAL;        -- WAL 下够用，避免每次提交 fsync
PRAGMA wal_autocheckpoint = 600;
PRAGMA temp_store = MEMORY;         -- 见下
PRAGMA cache_size = -32000;
```

`temp_store = MEMORY` 是踩坑后加的：默认行为下 SQLite 会把临时表/排序结果溢出到 `%TEMP%`，实测每天产生约 1800 个 `etilqs_*` 临时文件、累计 12GB+。改成内存后归零。

### 归档

`Store.Archive(days)` 把超过 `ArchiveAfterDays` 天的 `events` 行移进 `events_archive` 并删除原行，在每次启动时执行一次（失败不阻塞启动）。

## 6. 降级策略

ETW 需要 `SeSystemProfilePrivilege`，也就是必须管理员运行。非管理员或 ETW 不可用时：

1. `FrameClient` 为 `null` 或帧未就绪 → `AttributionEngine` 走**启发式归因**
2. UI 状态栏会显示当前精度模式，不静默欺骗用户
3. 会话启动前就存在的进程可能拿不到完整 exe 路径，显示为"（已退出，未抓到 exe 路径）"

## 7. 几个不直观的设计决策

**编译期字典做国际化**（`Forms/Strings.cs`）
早期用 EmbeddedResource JSON，因资源命名规则导致 key 裸奔显示给用户。改成 C# 编译期字典后，不存在"加载失败"这条路径。代价是加词条要改代码重新编译。

**`Environment.GetLogicalDrives()` 而非 `DriveInfo.IsReady`**
盘符枚举曾耗时 63 秒——`IsReady` 会对每个盘做实际 IO 探测，网络盘和空光驱会超时。换成 Win32 枚举后降到毫秒级。

**多文件自包含发布而非单文件**
单文件发布会把程序解压到 `%LOCALAPPDATA%\.net\`，与"绿色便携"的定位冲突。现在整个文件夹拷走即用。

**`V1AttributionShadow` 的存在意义**
归因算法从 v1 换到 v2 是要验证效果的，影子模块并行跑 v1 但只写每小时统计（`attribution_ab_hourly`），不落地事件行。等 A/B 数据证明 v2 稳定后即可移除。

## 8. 目录结构

```
src/DiskEye/
├── Etw/          ETW 采集侧：子进程工作器、帧协议、内核事件解析、句柄状态表
├── Monitoring/   监控引擎：聚合器、归因引擎、总控、FSW 监视器、ETW 帧客户端
├── Storage/      EventStore：SQLite 读写
├── Forms/        WinForms UI：主窗口、托盘、主题、图标、本地化字典
├── Config/       配置读写、开机自启
├── Models/       事件与配置的数据模型
└── Program.cs    入口：角色分流、单实例、唤醒
```

`src/KillDiskEye` 是独立的辅助工具（强制结束 DiskEye 进程），**未加入解决方案**，主要用于调试期清理卡死的进程。

## 9. 改代码时该动哪里

| 想做的事 | 入口 |
|---------|------|
| 加一个新标签页 | `Forms/MainForm.cs`（注意该文件已 1600+ 行，考虑拆分） |
| 改归因逻辑 | `Monitoring/AttributionEngine.cs`（改完看 `attribution_ab_hourly` 对照数据） |
| 加配置项 | `Models/AppConfig.cs` → `Config/AppConfigStore.cs` → 设置页 UI |
| 改告警规则 | `Monitoring/EventAggregator.cs` 的突增检测 + `AppConfig` 的阈值字段 |
| 加一种导出格式 | `Storage/EventStore.cs` 的导出方法 |
| 加翻译词条 | `Forms/Strings.cs` 的 `Zh` / `En` 两个字典，两边都要加 |

改动后请跑一遍：`dotnet test tests/DiskEye.Tests/DiskEye.Tests.csproj`（当前 67 个测试）。
