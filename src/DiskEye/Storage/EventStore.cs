using DiskEye.Models;
using Microsoft.Data.Sqlite;

namespace DiskEye.Storage;

/// <summary>
/// SQLite 存储。events 表存活跃数据，events_archive 存 &gt;30 天的历史。
/// 线程安全：内部用锁串行化写。读不阻塞写（SQLite WAL）。
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly string _connStr;
    private readonly object _writeLock = new();
    private SqliteConnection? _writeConn;

    public EventStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        InitSchema();
    }

    /// <summary>建表 + 索引。首次运行调用。</summary>
    private void InitSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA wal_autocheckpoint = 600;
            -- V0.9.6: 临时表/排序放内存。实测默认行为每天往 %TEMP% 溢出 ~1800 个 etilqs_*
            -- 临时文件共 12GB+（etilqs = sqlite 反写），temp_store=MEMORY 后归零
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -32000;

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                drive TEXT NOT NULL,
                path TEXT NOT NULL,
                event_type TEXT NOT NULL,
                size INTEGER NOT NULL,
                pid INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                process_path TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);
            CREATE INDEX IF NOT EXISTS ix_events_process ON events(process_name);

            -- V0.9: 真实写入字节（ETW FileIo/Write 1 秒聚合），榜单/今日写入的权威口径
            CREATE TABLE IF NOT EXISTS write_bytes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                pid INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                process_path TEXT NOT NULL DEFAULT '',
                path TEXT NOT NULL,
                folder TEXT NOT NULL,
                bytes INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_wb_ts ON write_bytes(ts);
            CREATE INDEX IF NOT EXISTS ix_wb_proc ON write_bytes(process_name, ts);
            CREATE INDEX IF NOT EXISTS ix_wb_folder ON write_bytes(folder, ts);

            -- V0.9: 每小时 A/B 归因对照（v1 vs v2），不逐行存储
            CREATE TABLE IF NOT EXISTS attribution_ab_hourly (
                hour TEXT PRIMARY KEY,
                fsw_events INTEGER NOT NULL DEFAULT 0,
                v1_named INTEGER NOT NULL DEFAULT 0,
                v2_named INTEGER NOT NULL DEFAULT 0,
                v2_writer INTEGER NOT NULL DEFAULT 0,
                v2_opener INTEGER NOT NULL DEFAULT 0,
                v2_recent INTEGER NOT NULL DEFAULT 0,
                v2_backfill INTEGER NOT NULL DEFAULT 0,
                heuristic INTEGER NOT NULL DEFAULT 0,
                unknown INTEGER NOT NULL DEFAULT 0,
                dropped_dirs INTEGER NOT NULL DEFAULT 0,
                etw_events_lost INTEGER NOT NULL DEFAULT 0,
                child_restarts INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS events_archive (
                id PRIMARY KEY,
                ts TEXT NOT NULL,
                drive TEXT NOT NULL,
                path TEXT NOT NULL,
                event_type TEXT NOT NULL,
                size INTEGER NOT NULL,
                pid INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                process_path TEXT NOT NULL,
                folder TEXT NOT NULL DEFAULT '',
                archived_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_archive_ts ON events_archive(ts);
        ";
        cmd.ExecuteNonQuery();

        // 启动时回收 WAL（v0.8.1 实测异常退出遗留 265MB WAL）
        try
        {
            using var ck = conn.CreateCommand();
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            ck.ExecuteNonQuery();
        }
        catch { /* 首次建库/锁定时忽略 */ }

        // V2: 加 folder 列（幂等 — 列存在时不报错）
        bool addedFolderCol = false;
        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE events ADD COLUMN folder TEXT NOT NULL DEFAULT ''";
            alterCmd.ExecuteNonQuery();
            addedFolderCol = true;
        }
        catch { /* 列已存在 */ }

        // 给 events 表建 folder 索引（V2: 让"按文件夹聚合"SQL 走索引）
        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_events_folder ON events(folder)";
        idxCmd.ExecuteNonQuery();

        // CC 修复: v0.1 → v0.2 升级时，旧 events 表里 folder 列是空字符串，
        // QueryFolderSummary 用 folder != '' 过滤会导致**旧事件不出现在文件夹重灾区**。
        // 在后台批量回填（一次写 1000 条，commit 一次，不阻塞 UI）。
        if (!addedFolderCol)
        {
            Task.Run(() => BackfillFolderColumn());
        }

        // V0.9: 加 source 列（归因来源，幂等——列已存在时忽略）
        try
        {
            using var alterSource = conn.CreateCommand();
            alterSource.CommandText = "ALTER TABLE events ADD COLUMN source TEXT NOT NULL DEFAULT ''";
            alterSource.ExecuteNonQuery();
        }
        catch { /* 列已存在 */ }
    }

    /// <summary>回填旧事件的 folder 字段。幂等（WHERE folder = '' 跳过已回填的）。</summary>
    private void BackfillFolderColumn()
    {
        try
        {
            int totalUpdated = 0;
            int batchSize = 1000;
            while (true)
            {
                int n;
                lock (_writeLock)
                {
                    using var conn = GetWriteConn();
                    using var tx = conn.BeginTransaction();
                    // 只更新空 folder 的行（幂等）
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        UPDATE events
                        SET folder = CASE
                            WHEN INSTR(SUBSTR(path, 4), '\') > 0
                            THEN SUBSTR(path, 1, 3 + INSTR(SUBSTR(path, 4), '\'))
                            ELSE path || '\'
                        END
                        WHERE folder = ''
                        LIMIT $limit
                    ";
                    cmd.Parameters.AddWithValue("$limit", batchSize);
                    n = cmd.ExecuteNonQuery();
                    tx.Commit();
                }
                totalUpdated += n;
                if (n < batchSize) break;  // 全部回填完
                Thread.Sleep(50);  // 让出 CPU
            }
            if (totalUpdated > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[EventStore] 回填 folder 字段 {totalUpdated} 条");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EventStore] folder 回填失败: {ex.Message}");
        }
    }

    /// <summary>批量插入。在锁内串行化。返回与入参同序的新行 id（V0.9 pending 回填用）。</summary>
    public IReadOnlyList<long> InsertBatch(IReadOnlyList<FileEvent> events)
    {
        var ids = new List<long>(events.Count);
        if (events.Count == 0) return ids;
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO events (ts, drive, path, folder, event_type, size, pid, process_name, process_path, source)
                VALUES ($ts, $drive, $path, $folder, $etype, $size, $pid, $pname, $ppath, $src)
            ";
            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pDr = cmd.CreateParameter(); pDr.ParameterName = "$drive"; cmd.Parameters.Add(pDr);
            var pPa = cmd.CreateParameter(); pPa.ParameterName = "$path"; cmd.Parameters.Add(pPa);
            var pFl = cmd.CreateParameter(); pFl.ParameterName = "$folder"; cmd.Parameters.Add(pFl);
            var pEt = cmd.CreateParameter(); pEt.ParameterName = "$etype"; cmd.Parameters.Add(pEt);
            var pSi = cmd.CreateParameter(); pSi.ParameterName = "$size"; cmd.Parameters.Add(pSi);
            var pPi = cmd.CreateParameter(); pPi.ParameterName = "$pid"; cmd.Parameters.Add(pPi);
            var pPn = cmd.CreateParameter(); pPn.ParameterName = "$pname"; cmd.Parameters.Add(pPn);
            var pPp = cmd.CreateParameter(); pPp.ParameterName = "$ppath"; cmd.Parameters.Add(pPp);
            var pSr = cmd.CreateParameter(); pSr.ParameterName = "$src"; cmd.Parameters.Add(pSr);

            using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";

            foreach (var e in events)
            {
                pTs.Value = e.Timestamp.ToString("O");
                pDr.Value = e.DriveLetter;
                pPa.Value = e.FullPath;
                // V2: 计算父目录 + 末尾反斜杠（路径前缀匹配更准）
                pFl.Value = GetFolder(e.FullPath);
                pEt.Value = e.EventType;
                pSi.Value = e.SizeBytes;
                pPi.Value = e.ProcessId;
                pPn.Value = e.ProcessName;
                pPp.Value = e.ProcessPath;
                pSr.Value = e.Source;
                cmd.ExecuteNonQuery();
                ids.Add(Convert.ToInt64(idCmd.ExecuteScalar()));
            }
            tx.Commit();
        }
        return ids;
    }

    /// <summary>计算父目录 + 末尾反斜杠。"C:\foo\bar.txt" → "C:\foo\"</summary>
    private static string GetFolder(string fullPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            return string.IsNullOrEmpty(dir) ? "" : dir + Path.DirectorySeparatorChar;
        }
        catch { return ""; }
    }

    /// <summary>凶手指控榜：从指定时刻起的所有事件按进程聚合。</summary>
    public List<ProcessSummary> QueryProcessSummary(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT process_name, process_path, COUNT(*), COALESCE(SUM(size), 0), MAX(ts)
            FROM events
            WHERE ts >= $since
            GROUP BY process_name
            ORDER BY SUM(size) DESC, COUNT(*) DESC
        ";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));

        var list = new List<ProcessSummary>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new ProcessSummary(
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.GetInt64(2),
                rdr.GetInt64(3),
                DateTime.Parse(rdr.GetString(4))
            ));
        }
        return list;
    }

    /// <summary>V2: 文件夹重灾区 — 按 folder 列 GROUP BY，按 SUM(size) DESC 排序。</summary>
    public List<FolderSummary> QueryFolderSummary(DateTime since, int top = 100)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT folder, COUNT(*), COALESCE(SUM(size), 0), MAX(ts)
            FROM events
            WHERE ts >= $since
              AND event_type IN ('Created','Modified','Renamed')
              AND folder != ''
            GROUP BY folder
            ORDER BY SUM(size) DESC, COUNT(*) DESC
            LIMIT $top
        ";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$top", top);

        var list = new List<FolderSummary>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new FolderSummary(
                rdr.GetString(0),
                rdr.GetInt64(1),
                rdr.GetInt64(2),
                DateTime.Parse(rdr.GetString(3))
            ));
        }
        return list;
    }

    /// <summary>该进程的所有事件明细（按时间倒序，最多 1000 条）。</summary>
    public List<FileEvent> QueryEventsByProcess(string processName, DateTime since, int limit = 1000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, ts, drive, path, event_type, size, pid, process_name, process_path
            FROM events
            WHERE process_name = $pname AND ts >= $since
            ORDER BY ts DESC
            LIMIT $limit
        ";
        cmd.Parameters.AddWithValue("$pname", processName);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<FileEvent>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new FileEvent(
                rdr.GetInt64(0),
                DateTime.Parse(rdr.GetString(1)),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.GetString(4),
                rdr.GetInt64(5),
                rdr.GetInt32(6),
                rdr.GetString(7),
                rdr.GetString(8)
            ));
        }
        return list;
    }

    /// <summary>V5.4: 该 PID 的所有事件（按时间倒序）。</summary>
    public List<FileEvent> QueryEventsByPid(int pid, DateTime since, int limit = 1000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, ts, drive, path, event_type, size, pid, process_name, process_path
            FROM events
            WHERE pid = $pid AND ts >= $since
            ORDER BY ts DESC
            LIMIT $limit
        ";
        cmd.Parameters.AddWithValue("$pid", pid);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<FileEvent>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new FileEvent(
                rdr.GetInt64(0),
                DateTime.Parse(rdr.GetString(1)),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.GetString(4),
                rdr.GetInt64(5),
                rdr.GetInt32(6),
                rdr.GetString(7),
                rdr.GetString(8)
            ));
        }
        return list;
    }

    /// <summary>原始事件流（按时间倒序）。</summary>
    public List<FileEvent> QueryRecentEvents(int limit = 500)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, ts, drive, path, event_type, size, pid, process_name, process_path
            FROM events
            ORDER BY ts DESC
            LIMIT $limit
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<FileEvent>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new FileEvent(
                rdr.GetInt64(0),
                DateTime.Parse(rdr.GetString(1)),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.GetString(4),
                rdr.GetInt64(5),
                rdr.GetInt32(6),
                rdr.GetString(7),
                rdr.GetString(8)
            ));
        }
        return list;
    }

    /// <summary>今日总写入字节（用于托盘 tooltip）。</summary>
    public long QueryTodayBytes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(size), 0)
            FROM events
            WHERE ts >= $start AND event_type IN ('Created','Modified','Renamed')
        ";
        cmd.Parameters.AddWithValue("$start", DateTime.Today.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>V5.3: 24 小时桶的写入字节数（用于图表）。返回 24 个桶，索引 0..23。</summary>
    public long[] QueryHourlyBytes(DateTime since)
    {
        var buckets = new long[24];
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT CAST(strftime('%H', ts) AS INTEGER) AS hour,
                   COALESCE(SUM(size), 0) AS bytes
            FROM events
            WHERE ts >= $since AND event_type IN ('Created','Modified','Renamed')
            GROUP BY hour
        ";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            int hour = rdr.GetInt32(0);
            if (hour >= 0 && hour < 24) buckets[hour] = rdr.GetInt64(1);
        }
        return buckets;
    }

    /// <summary>V0.9.1: 进程树查询。
    /// v0.8 实现对每个 pid 发起一次 WMI（2s 超时），872 个 pid 可冻结 UI 线程近 30 分钟。
    /// 现在：一次 WMI 全量枚举建 pid→ppid 映射；名字优先取 DB 已记录值（进程退出也有名字）；
    /// 字节取 write_bytes。调用方必须在后台线程调用，结果缓存 30 秒。</summary>
    private List<ProcessTreeNode>? _processTreeCache;
    private DateTime _processTreeCacheAt = DateTime.MinValue;
    private static readonly TimeSpan _processTreeCacheTtl = TimeSpan.FromSeconds(30);

    public List<ProcessTreeNode> QueryProcessTree(DateTime since)
    {
        var cacheKey = since.Date;
        if (_processTreeCache != null
            && DateTime.Now - _processTreeCacheAt < _processTreeCacheTtl
            && _lastProcessTreeSinceKey == cacheKey)
        {
            return _processTreeCache;
        }

        // 1. events 按 pid 聚合事件数/最近活动 + 该 pid 已知进程名（非 unknown 优先）
        Dictionary<int, (long Count, DateTime Last, string Name)> byPid = new();
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT pid, COUNT(*), MAX(ts),
                       COALESCE(MAX(CASE WHEN process_name<>'unknown' AND process_name<>'' THEN process_name END), 'unknown')
                FROM events
                WHERE ts >= $since AND pid > 0
                GROUP BY pid";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                byPid[rdr.GetInt32(0)] = (rdr.GetInt64(1), DateTime.Parse(rdr.GetString(2)), rdr.GetString(3));
            }
        }

        // 2. write_bytes 按 pid 聚合真实写入
        var bytesByPid = new Dictionary<int, long>();
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT pid, COALESCE(SUM(bytes),0)
                FROM write_bytes WHERE ts >= $since GROUP BY pid";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                bytesByPid[rdr.GetInt32(0)] = rdr.GetInt64(1);
        }

        // 3. 一次 WMI 全量枚举建 pid→ppid 映射（替代 N 次查询；整体 8 秒超时）
        var parentMap = LoadParentPidMap();

        // 4. 合并
        var result = new List<ProcessTreeNode>();
        foreach (var kv in byPid)
        {
            int pid = kv.Key;
            var dbName = kv.Value.Name;
            string name = dbName != "unknown" ? dbName : "unknown";
            string path = "";
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                try { name = System.IO.Path.GetFileName(p.MainModule?.FileName ?? p.ProcessName + ".exe"); }
                catch { if (name == "unknown") name = p.ProcessName + ".exe"; }
                try { path = p.MainModule?.FileName ?? ""; } catch { }
            }
            catch { if (name == "unknown") name = "(已退出)"; }

            int? parentPid = parentMap.TryGetValue(pid, out var pp) ? pp : null;
            string parentName = "—";
            if (parentPid is int ppid && ppid > 0 && byPid.TryGetValue(ppid, out var pv) && pv.Name != "unknown")
                parentName = pv.Name;

            bytesByPid.TryGetValue(pid, out var wb);
            result.Add(new ProcessTreeNode(
                pid, parentPid, parentName, name, path,
                kv.Value.Count, wb, kv.Value.Last));
        }

        result.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

        _processTreeCache = result;
        _processTreeCacheAt = DateTime.Now;
        _lastProcessTreeSinceKey = cacheKey;
        return result;
    }

    /// <summary>一次 WMI 查询拿全量 pid→父pid 映射；失败/超时返回空字典（绝不拖垮调用方）。</summary>
    private static Dictionary<int, int> LoadParentPidMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            var task = Task.Run(() =>
            {
                var local = new Dictionary<int, int>();
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT ProcessId,ParentProcessId FROM Win32_Process");
                    using var results = searcher.Get();
                    foreach (System.Management.ManagementObject mo in results)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            int ppid = Convert.ToInt32(mo["ParentProcessId"]);
                            local[pid] = ppid;
                        }
                        catch { }
                    }
                }
                catch { }
                return local;
            });
            if (task.Wait(TimeSpan.FromSeconds(8))) map = task.Result;
        }
        catch { }
        return map;
    }

    /// <summary>V0.9.1: 用同 pid 已知名字补全 unknown（events + write_bytes），后台节流调用。</summary>
    public int BackfillNamesByPid()
    {
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var tx = conn.BeginTransaction();
            int n1, n2;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE events
                    SET process_name = (
                        SELECT e2.process_name FROM events e2
                        WHERE e2.pid = events.pid AND e2.process_name NOT IN ('unknown','')
                        ORDER BY e2.id DESC LIMIT 1)
                    WHERE (process_name='unknown' OR process_name='') AND pid > 0
                      AND EXISTS (SELECT 1 FROM events e3 WHERE e3.pid=events.pid AND e3.process_name NOT IN ('unknown',''))
                    LIMIT 5000";
                n1 = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE write_bytes
                    SET process_name = (
                        SELECT e.process_name FROM events e
                        WHERE e.pid = write_bytes.pid AND e.process_name NOT IN ('unknown','')
                        ORDER BY e.id DESC LIMIT 1)
                    WHERE process_name='unknown' AND pid > 0
                      AND EXISTS (SELECT 1 FROM events e2 WHERE e2.pid=write_bytes.pid AND e2.process_name NOT IN ('unknown',''))
                    LIMIT 5000";
                n2 = cmd.ExecuteNonQuery();
            }
            int n3, n4;
            // V0.9.6: exe 路径回填——同 pid 任一行有 exe 路径 → 补到空路径行（events + write_bytes）
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE events
                    SET process_path = (
                        SELECT e2.process_path FROM events e2
                        WHERE e2.pid = events.pid AND e2.process_path <> ''
                        ORDER BY e2.id DESC LIMIT 1)
                    WHERE (process_path='' OR process_path IS NULL) AND pid > 0
                      AND EXISTS (SELECT 1 FROM events e3 WHERE e3.pid=events.pid AND e3.process_path <> '')
                    LIMIT 5000";
                n3 = cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE write_bytes
                    SET process_path = (
                        SELECT w2.process_path FROM write_bytes w2
                        WHERE w2.pid = write_bytes.pid AND w2.process_path <> ''
                        ORDER BY w2.id DESC LIMIT 1)
                    WHERE (process_path='' OR process_path IS NULL) AND pid > 0
                      AND EXISTS (SELECT 1 FROM write_bytes w3 WHERE w3.pid=write_bytes.pid AND w3.process_path <> '')
                    LIMIT 5000";
                n4 = cmd.ExecuteNonQuery();
            }
            int n5;
            // V0.9.6: 同路径已知写者回填——短命进程没抓到名字，但同文件其他行有名字
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE events
                    SET pid = (SELECT w.pid FROM write_bytes w WHERE w.path = events.path AND w.pid > 0 ORDER BY w.id DESC LIMIT 1),
                        process_name = (SELECT w.process_name FROM write_bytes w WHERE w.path = events.path AND w.process_name NOT IN ('unknown','') ORDER BY w.id DESC LIMIT 1)
                    WHERE (process_name='unknown') AND pid = 0
                      AND EXISTS (SELECT 1 FROM write_bytes w2 WHERE w2.path = events.path AND w2.process_name NOT IN ('unknown',''))
                    LIMIT 3000";
                n5 = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n1 + n2 + n3 + n4 + n5;
        }
    }

    /// <summary>V6.2 #2: 统计信息（DB 大小 + 事件总数 + 时间范围）。CC P3 修复。</summary>
    public sealed record DbStats(long DbSizeBytes, long EventCount, DateTime? FirstTs, DateTime? LastTs);

    /// <summary>V0.9: 每小时 A/B 归因计数（UpsertAbHourly）。</summary>
    public sealed record AbHourlyStats(
        string Hour,
        long FswEvents, long V1Named, long V2Named,
        long V2Writer, long V2Opener, long V2Recent, long V2Backfill,
        long Heuristic, long Unknown, long DroppedDirs,
        long EtwEventsLost, long ChildRestarts);

    public DbStats GetStats()
    {
        // CC 三审 P2 修复：路径提取用 SqliteConnectionStringBuilder，不靠 Replace 字符串拼接
        var dataSource = new SqliteConnectionStringBuilder(_connStr).DataSource;
        long dbSize = File.Exists(dataSource) ? new FileInfo(dataSource).Length : 0;

        long totalEvents = 0;
        DateTime? firstTs = null;
        DateTime? lastTs = null;
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MIN(ts), MAX(ts) FROM events";
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                totalEvents = rdr.GetInt64(0);
                if (!rdr.IsDBNull(1)) firstTs = DateTime.Parse(rdr.GetString(1));
                if (!rdr.IsDBNull(2)) lastTs = DateTime.Parse(rdr.GetString(2));
            }
        }
        return new DbStats(dbSize, totalEvents, firstTs, lastTs);
    }

    private DateTime _lastProcessTreeSinceKey = DateTime.MinValue;

    private static int? GetParentPidViaWmi(int pid)
    {
        return GetParentPidViaWmiWithTimeout(pid, TimeSpan.FromSeconds(2));
    }

    /// <summary>CC #1 修复：WMI 加超时，避免某个 PID 查询卡住整个进程树。</summary>
    private static int? GetParentPidViaWmiWithTimeout(int pid, TimeSpan timeout)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");

            // 在后台线程跑搜索，主线程超时
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var results = searcher.Get();
                    foreach (var mo in results)
                    {
                        return (int?)Convert.ToInt32(mo["ParentProcessId"]);
                    }
                }
                catch { }
                return null;
            });
            if (task.Wait(timeout))
            {
                return task.Result;
            }
        }
        catch { }
        return null;
    }

    /// <summary>V4.1: 导出全部事件到 CSV 文件。返回写入的行数。</summary>
    public int ExportToCsv(string csvPath, DateTime? since = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (since.HasValue)
        {
            cmd.CommandText = @"
                SELECT id, ts, drive, path, folder, event_type, size, pid, process_name, process_path
                FROM events
                WHERE ts >= $since
                ORDER BY ts ASC
            ";
            cmd.Parameters.AddWithValue("$since", since.Value.ToString("O"));
        }
        else
        {
            cmd.CommandText = @"
                SELECT id, ts, drive, path, folder, event_type, size, pid, process_name, process_path
                FROM events
                ORDER BY ts ASC
            ";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        int total = 0;
        using var writer = new StreamWriter(csvPath, append: false, System.Text.Encoding.UTF8);
        // Excel 友好的 UTF-8 BOM
        writer.WriteLine("﻿id,time,drive,folder,event,size_bytes,process_name,pid,full_path,exe_path");

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var id = rdr.GetInt64(0);
            var ts = DateTime.Parse(rdr.GetString(1));
            var drive = rdr.GetString(2);
            var path = rdr.GetString(3);
            var folder = rdr.IsDBNull(4) ? "" : rdr.GetString(4);
            var etype = rdr.GetString(5);
            var size = rdr.GetInt64(6);
            var pid = rdr.GetInt32(7);
            var pname = rdr.GetString(8);
            var ppath = rdr.GetString(9);

            writer.Write(id); writer.Write(',');
            writer.Write(ts.ToString("yyyy-MM-dd HH:mm:ss")); writer.Write(',');
            writer.Write(CsvEscape(drive)); writer.Write(',');
            writer.Write(CsvEscape(folder)); writer.Write(',');
            writer.Write(CsvEscape(etype)); writer.Write(',');
            writer.Write(size); writer.Write(',');
            writer.Write(CsvEscape(pname)); writer.Write(',');
            writer.Write(pid); writer.Write(',');
            writer.Write(CsvEscape(path)); writer.Write(',');
            writer.WriteLine(CsvEscape(ppath));

            total++;
        }
        return total;
    }

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // CSV 转义：含逗号 / 引号 / 换行的字段用双引号包裹，引号内部双写
        bool needQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>归档 &gt; N 天的记录。返回归档条数。</summary>
    public int Archive(int days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO events_archive
                    (id, ts, drive, path, event_type, size, pid, process_name, process_path, archived_at)
                SELECT id, ts, drive, path, event_type, size, pid, process_name, process_path, $now
                FROM events
                WHERE ts < $cutoff;
                DELETE FROM events WHERE ts < $cutoff;
                DELETE FROM write_bytes WHERE ts < $cutoff;
            ";
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            int n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    // ———————————————— V0.9：真实写入字节 / 回填 / A-B / checkpoint ————————————————

    /// <summary>批量写入 ETW Write 聚合样本（字节权威口径）。</summary>
    public void InsertWriteBatch(IReadOnlyList<WriteSample> samples)
    {
        if (samples.Count == 0) return;
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO write_bytes (ts, pid, process_name, process_path, path, folder, bytes)
                VALUES ($ts, $pid, $pname, $ppath, $path, $folder, $bytes)";
            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pPid = cmd.CreateParameter(); pPid.ParameterName = "$pid"; cmd.Parameters.Add(pPid);
            var pName = cmd.CreateParameter(); pName.ParameterName = "$pname"; cmd.Parameters.Add(pName);
            var pPath = cmd.CreateParameter(); pPath.ParameterName = "$ppath"; cmd.Parameters.Add(pPath);
            var pFile = cmd.CreateParameter(); pFile.ParameterName = "$path"; cmd.Parameters.Add(pFile);
            var pFolder = cmd.CreateParameter(); pFolder.ParameterName = "$folder"; cmd.Parameters.Add(pFolder);
            var pBytes = cmd.CreateParameter(); pBytes.ParameterName = "$bytes"; cmd.Parameters.Add(pBytes);
            foreach (var s in samples)
            {
                pTs.Value = s.Timestamp.ToString("O");
                pPid.Value = s.Pid;
                pName.Value = s.ProcessName;
                pPath.Value = s.ProcessPath;
                pFile.Value = s.FullPath;
                pFolder.Value = s.Folder;
                pBytes.Value = s.Bytes;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>属主晚到：按行 id 批量回填 pid/名字/来源。返回受影响行数。</summary>
    public int BackfillRows(IReadOnlyList<long> ids, int pid, string name, string exePath, string source)
    {
        if (ids.Count == 0) return 0;
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var placeholders = string.Join(",", ids.Select((_, i) => "$id" + i));
            cmd.CommandText = $@"
                UPDATE events SET pid=$pid, process_name=$pname, process_path=$ppath, source=$src
                WHERE id IN ({placeholders}) AND (pid = 0 OR process_name = 'unknown')";
            cmd.Parameters.AddWithValue("$pid", pid);
            cmd.Parameters.AddWithValue("$pname", name);
            cmd.Parameters.AddWithValue("$ppath", exePath);
            cmd.Parameters.AddWithValue("$src", source);
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue("$id" + i, ids[i]);
            int n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
    }

    /// <summary>每小时 A/B 归因计数（同小时累加）。</summary>
    public void UpsertAbHourly(AbHourlyStats s)
    {
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO attribution_ab_hourly
                  (hour, fsw_events, v1_named, v2_named, v2_writer, v2_opener, v2_recent,
                   v2_backfill, heuristic, unknown, dropped_dirs, etw_events_lost, child_restarts)
                VALUES ($h,$fsw,$v1,$v2,$w,$o,$r,$bf,$he,$un,$dr,$el,$cr)
                ON CONFLICT(hour) DO UPDATE SET
                  fsw_events=fsw_events+excluded.fsw_events,
                  v1_named=v1_named+excluded.v1_named,
                  v2_named=v2_named+excluded.v2_named,
                  v2_writer=v2_writer+excluded.v2_writer,
                  v2_opener=v2_opener+excluded.v2_opener,
                  v2_recent=v2_recent+excluded.v2_recent,
                  v2_backfill=v2_backfill+excluded.v2_backfill,
                  heuristic=heuristic+excluded.heuristic,
                  unknown=unknown+excluded.unknown,
                  dropped_dirs=dropped_dirs+excluded.dropped_dirs,
                  etw_events_lost=MAX(etw_events_lost,excluded.etw_events_lost),
                  child_restarts=MAX(child_restarts,excluded.child_restarts)";
            cmd.Parameters.AddWithValue("$h", s.Hour);
            cmd.Parameters.AddWithValue("$fsw", s.FswEvents);
            cmd.Parameters.AddWithValue("$v1", s.V1Named);
            cmd.Parameters.AddWithValue("$v2", s.V2Named);
            cmd.Parameters.AddWithValue("$w", s.V2Writer);
            cmd.Parameters.AddWithValue("$o", s.V2Opener);
            cmd.Parameters.AddWithValue("$r", s.V2Recent);
            cmd.Parameters.AddWithValue("$bf", s.V2Backfill);
            cmd.Parameters.AddWithValue("$he", s.Heuristic);
            cmd.Parameters.AddWithValue("$un", s.Unknown);
            cmd.Parameters.AddWithValue("$dr", s.DroppedDirs);
            cmd.Parameters.AddWithValue("$el", s.EtwEventsLost);
            cmd.Parameters.AddWithValue("$cr", s.ChildRestarts);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>V0.9.1 统一行（榜单）：事件数来自 events、写入字节来自 write_bytes。
    /// V0.9.6 加 TopFolder（该进程写入最多的目录——"往哪倒垃圾"）。</summary>
    public sealed record UnifiedRow(string Name, string Path, long EventCount, long WriteBytes,
        DateTime LastActivity, string TopFolder = "");

    /// <summary>凶手指控榜合并口径：events 事件数（含删除/改名等无写入活动）+ write_bytes 真实字节。</summary>
    public List<UnifiedRow> QueryProcessUnified(DateTime since)
    {
        var rows = new Dictionary<string, UnifiedRow>(StringComparer.OrdinalIgnoreCase);
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT process_name, MAX(process_path), COUNT(*), MAX(ts)
                FROM events WHERE ts >= $since
                GROUP BY process_name";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var n = rdr.GetString(0);
                rows[n] = new UnifiedRow(n, rdr.GetString(1), rdr.GetInt64(2), 0,
                    DateTime.Parse(rdr.GetString(3)));
            }
        }
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT process_name, SUM(bytes), MAX(ts)
                FROM write_bytes WHERE ts >= $since GROUP BY process_name";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var n = rdr.GetString(0);
                var wb = rdr.GetInt64(1);
                var last = DateTime.Parse(rdr.GetString(2));
                // 进程可能只在 write_bytes 出现（写字节但无今天的 events 行），cur 为 null
                rows.TryGetValue(n, out var cur);
                rows[n] = new UnifiedRow(n, cur?.Path ?? "", cur?.EventCount ?? 0, wb,
                    cur == null || last > cur.LastActivity ? last : cur.LastActivity);
            }
        }

        // V0.9.6: 每进程的"主要写入目录"（write_bytes 按 folder 聚合取 top1，"往哪倒垃圾"）
        var topFolders = new Dictionary<string, (long Bytes, string Folder)>(StringComparer.OrdinalIgnoreCase);
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT process_name, folder, SUM(bytes) AS b
                FROM write_bytes WHERE ts >= $since AND folder <> ''
                GROUP BY process_name, folder";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var n = rdr.GetString(0);
                var f = rdr.GetString(1);
                var b = rdr.GetInt64(2);
                if (!topFolders.TryGetValue(n, out var cur) || b > cur.Bytes)
                    topFolders[n] = (b, f);
            }
        }

        return rows.Values
            .Select(r => topFolders.TryGetValue(r.Name, out var tf)
                ? r with { TopFolder = tf.Folder }
                : r)
            .OrderByDescending(r => r.WriteBytes)
            .ThenByDescending(r => r.EventCount)
            .ToList();
    }

    /// <summary>文件夹重灾区合并口径。</summary>
    public List<UnifiedRow> QueryFolderUnified(DateTime since, int top = 200)
    {
        var rows = new Dictionary<string, UnifiedRow>(StringComparer.OrdinalIgnoreCase);
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT folder, COUNT(*), MAX(ts) FROM events
                WHERE ts >= $since AND folder <> ''
                GROUP BY folder";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var f = rdr.GetString(0);
                rows[f] = new UnifiedRow(f, "", rdr.GetInt64(1), 0, DateTime.Parse(rdr.GetString(2)));
            }
        }
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT folder, SUM(bytes), MAX(ts) FROM write_bytes
                WHERE ts >= $since AND folder <> '' GROUP BY folder";
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var f = rdr.GetString(0);
                var wb = rdr.GetInt64(1);
                var last = DateTime.Parse(rdr.GetString(2));
                rows.TryGetValue(f, out var cur);
                rows[f] = new UnifiedRow(f, "", cur?.EventCount ?? 0, wb,
                    cur == null || last > cur.LastActivity ? last : cur.LastActivity);
            }
        }
        return rows.Values
            .OrderByDescending(r => r.WriteBytes)
            .ThenByDescending(r => r.EventCount)
            .Take(top)
            .ToList();
    }

    /// <summary>凶手指控榜字节口径（ETW 真实写入）。</summary>
    public List<ProcessSummary> QueryProcessBytes(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT process_name, MAX(process_path), COUNT(*), COALESCE(SUM(bytes),0), MAX(ts)
            FROM write_bytes WHERE ts >= $since
            GROUP BY process_name ORDER BY SUM(bytes) DESC, COUNT(*) DESC";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        var list = new List<ProcessSummary>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new ProcessSummary(rdr.GetString(0), rdr.GetString(1), rdr.GetInt64(2), rdr.GetInt64(3),
                DateTime.Parse(rdr.GetString(4))));
        return list;
    }

    /// <summary>文件夹重灾区字节口径。</summary>
    public List<FolderSummary> QueryFolderBytes(DateTime since, int top = 100)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT folder, COUNT(*), COALESCE(SUM(bytes),0), MAX(ts)
            FROM write_bytes WHERE ts >= $since AND folder != ''
            GROUP BY folder ORDER BY SUM(bytes) DESC, COUNT(*) DESC LIMIT $top";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$top", top);
        var list = new List<FolderSummary>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new FolderSummary(rdr.GetString(0), rdr.GetInt64(1), rdr.GetInt64(2),
                DateTime.Parse(rdr.GetString(3))));
        return list;
    }

    /// <summary>今日真实写入字节总量。</summary>
    public long QueryTodayWriteBytes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes),0) FROM write_bytes WHERE ts >= $start";
        cmd.Parameters.AddWithValue("$start", DateTime.Today.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// V0.9.8: 清理非监控盘的 write_bytes 历史行（盘符过滤上线前的 bug 期数据，
    /// 否则"文件夹重灾区"首名会一直是修复前的 C:\...\Temp）。返回删除行数。
    /// </summary>
    public int PurgeWritesOutsideDrives(IReadOnlyCollection<string> allowedDrives)
    {
        if (allowedDrives.Count == 0) return 0;
        lock (_writeLock)
        {
            using var conn = GetWriteConn();
            using var cmd = conn.CreateCommand();
            var conds = string.Join(" OR ", allowedDrives.Select((_, i) => $"upper(substr(path,1,1)) <> $d{i}"));
            cmd.CommandText = $"DELETE FROM write_bytes WHERE {conds}";
            int i = 0;
            foreach (var d in allowedDrives)
                cmd.Parameters.AddWithValue("$d" + i++, d.ToUpperInvariant());
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>24 小时桶真实写入字节（图表）。</summary>
    public long[] QueryHourlyWriteBytes(DateTime since)
    {
        var buckets = new long[24];
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT CAST(strftime('%H', ts) AS INTEGER), COALESCE(SUM(bytes),0)
            FROM write_bytes WHERE ts >= $since GROUP BY 1";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            int hour = rdr.GetInt32(0);
            if (hour >= 0 && hour < 24) buckets[hour] = rdr.GetInt64(1);
        }
        return buckets;
    }

    /// <summary>每 pid 真实写入字节（进程树 tab）。返回 pid → (name, bytes, samples)。</summary>
    public List<(int Pid, string Name, long Bytes, long Count)> QueryPidBytes(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pid, MAX(process_name), COALESCE(SUM(bytes),0), COUNT(*)
            FROM write_bytes WHERE ts >= $since GROUP BY pid";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        var list = new List<(int, string, long, long)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetInt64(2), rdr.GetInt64(3)));
        return list;
    }

    private DateTime _lastCheckpoint = DateTime.MinValue;
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(30);

    /// <summary>30 分钟节流的 PASSIVE checkpoint（控制 WAL 体积，不阻塞读写）。</summary>
    public void MaybePeriodicCheckpoint()
    {
        var now = DateTime.Now;
        if (now - _lastCheckpoint < CheckpointInterval) return;
        _lastCheckpoint = now;
        try
        {
            lock (_writeLock)
            {
                using var conn = GetWriteConn();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* checkpoint 失败不致命 */ }
    }

    private SqliteConnection GetWriteConn()
    {
        if (_writeConn == null || _writeConn.State != System.Data.ConnectionState.Open)
        {
            _writeConn = Open();
        }
        return _writeConn;
    }

    public void Dispose()
    {
        _writeConn?.Dispose();
    }
}