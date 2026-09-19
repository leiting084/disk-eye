using DiskEye.Etw;

namespace DiskEye.Monitoring;

/// <summary>
/// A/B 对照用的 v0.8.1 影子归因：只吃打开帧（OP）、4 秒 TTL、路径先到先得，
/// 不看 writer/recent/删除帧、不过滤目录。仅用于统计每小时 v1 能否具名，绝不影响 v2 结果。
/// </summary>
public sealed class V1AttributionShadow
{
    private readonly Dictionary<string, (int Pid, long ExpireMs)> _map = new();
    private readonly Func<long> _now;
    private const long TtlMs = 4_000;

    public V1AttributionShadow(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);

    public void ApplyFrame(Frame f)
    {
        if (f.Kind != FrameKind.Open || string.IsNullOrEmpty(f.Path)) return;
        var k = f.Path.TrimEnd('\\').ToLowerInvariant();
        lock (_map) _map[k] = (f.Pid, _now() + TtlMs);
    }

    /// <summary>按 v0.8.1 规则该路径此刻能否具名。</summary>
    public bool IsNamed(string path)
    {
        var k = (path ?? "").TrimEnd('\\').ToLowerInvariant();
        var now = _now();
        lock (_map)
        {
            Prune(now);
            return _map.TryGetValue(k, out var v) && v.ExpireMs > now;
        }
    }

    private void Prune(long now)
    {
        foreach (var key in _map.Keys.Where(k => _map[k].ExpireMs <= now).ToList())
            _map.Remove(key);
    }
}
