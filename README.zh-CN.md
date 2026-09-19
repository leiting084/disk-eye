# DiskEye - Windows 磁盘 I/O 监控工具

[English](README.md)

基于 ETW（Windows 事件跟踪）的实时磁盘 I/O 监控工具，精确归因到进程级别。

## 功能特性

- **精确归因**：使用 ETW Kernel-File Provider，准确追踪哪个进程写了哪个文件
- **8 个综合视图**：
  - 凶手指控榜 - 按写入量排名的进程
  - 文件夹重灾区 - 写入活动最多的目录
  - 事件流 - 实时文件操作事件
  - 今日趋势 - 24 小时写入活动可视化
  - 进程树 - 父子进程关系
  - 垃圾清理 - 安全清理临时文件和浏览器缓存
  - 工具 - 数据导出和归档
  - 设置 - 配置和偏好设置
- **实时监控**：3 秒刷新间隔，保持滚动位置
- **多语言支持**：中英文运行时切换（无需重启）
- **系统托盘集成**：最小化到托盘、实时字节计数、快捷菜单
- **绿色便携**：无需安装，单文件夹部署，不污染系统
- **智能告警**：突增检测 + 气泡通知，发现异常写入活动
- **数据管理**：SQLite 存储（WAL 模式）、自动归档（>30天）、CSV 导出
- **性能优化**：快速启动（<2秒）、JIT 预热、高效盘符枚举

## 截图

![主窗口](docs/screenshots/main-window.png)
![设置](docs/screenshots/settings.png)
![托盘菜单](docs/screenshots/tray-menu.png)

## 下载

从 [Releases](../../releases) 页面下载最新版本。

**便携版**：解压 zip 文件到任意文件夹，运行 `DiskEye.exe`。无需安装。

## 系统要求

- Windows 10/11（64位）
- 管理员权限（ETW 需要）
- .NET 8.0 运行时（自包含版本已内置）

## 快速开始

1. 下载并解压便携版
2. 以管理员身份运行 `DiskEye.exe`
3. 在设置标签页选择要监控的盘符
4. 在多个标签页中查看实时磁盘活动

## 从源码构建

### 先决条件

- .NET 8.0 SDK
- Windows 10/11（64位）

### 构建命令

```bash
# 调试构建
dotnet build src/DiskEye/DiskEye.csproj

# 发布构建
dotnet publish src/DiskEye/DiskEye.csproj -c Release -o publish
```

发布输出将在 `publish` 文件夹中。

## 架构

### 核心组件

- **EtwProcessResolver**：管理 ETW 会话和进程跟踪
- **EventAggregator**：按进程和文件夹聚合文件事件
- **AttributionEngine**：使用 ETW 数据将文件写入归因到进程
- **EventStore**：基于 SQLite 的存储，WAL 模式支持并发访问
- **MonitorService**：协调所有监控组件

### 数据流

```
ETW 事件 → EventAggregator → AttributionEngine → EventStore → UI 刷新
                                  ↓
                            进程解析
```

详见 [docs/architecture.md](docs/architecture.md) 了解详细技术文档。

## 配置

配置存储在可执行文件旁边的 `config.json` 中：

```json
{
  "MonitoredDrives": ["C", "D"],
  "AutoStart": false,
  "Language": "zh",
  "IncludePrefixes": [],
  "IgnorePrefixes": [],
  "ProcessSurgeThresholdBytes": 104857600,
  "FolderSurgeThresholdBytes": 524288000,
  "SurgeWindowSeconds": 60,
  "SurgeCooldownMinutes": 30
}
```

## 隐私

- 所有数据本地存储在 SQLite 数据库中
- 无遥测或数据收集
- 无网络连接（除非启用 GitHub 更新）
- 开源 - 你可以审计代码

## 贡献

欢迎贡献！详见 [CONTRIBUTING.md](CONTRIBUTING.md) 了解指南。

### 开发环境设置

1. 克隆仓库
2. 在 Visual Studio 或 VS Code 中打开 `src/DiskEye/DiskEye.csproj`
3. 以管理员身份构建和运行

### 代码风格

- 遵循 C# 编码规范
- 使用有意义的变量名
- 为公共 API 添加 XML 文档
- 为新功能编写单元测试

## 路线图

- [ ] 深色模式支持
- [ ] 历史数据分析
- [ ] 自定义告警规则
- [ ] 导出到多种格式（Excel、JSON）
- [ ] 自定义分析器插件系统
- [ ] 性能分析集成

## 已知问题

- ETW 需要管理员权限
- 某些系统进程可能没有完整的路径信息
- 高写入活动可能导致临时 UI 延迟（通过后台刷新缓解）

## 故障排除

### "ETW 子进程不可用"

此消息表示 ETW 监控子进程遇到错误。应用程序将自动回退到启发式模式。

**解决方案**：
- 确保以管理员身份运行
- 检查 Windows 事件查看器中的 ETW 相关错误
- 重启应用程序

### 启动缓慢

首次启动可能需要 2-3 秒，因为 JIT 编译。后续启动更快。

### 缺少进程图标

某些进程可能没有可访问的图标。将显示默认图标。

## 许可证

本项目基于 MIT 许可证开源 - 详见 [LICENSE](LICENSE) 文件。

## 致谢

- [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/) - ETW 事件处理
- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/) - SQLite 数据库访问
- Windows ETW Kernel-File Provider 提供精确的 I/O 归因

## 支持

- **问题**：[GitHub Issues](../../issues)
- **讨论**：[GitHub Discussions](../../discussions)

---

**注意**：此工具专为监控和诊断目的设计。请负责任地使用并尊重系统性能。
