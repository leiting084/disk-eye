using System.Text.Json;

namespace DiskEye.Etw;

/// <summary>
/// V1 主从 stdout 行协议编解码。
/// 首行协议版本 <see cref="ProtocolVersion"/>；之后每行一帧（无制表符/换行——字段经 Sanitize）。
/// 解析器在 ReadLoop 热路径调用：只返回 bool，永不抛异常。
/// </summary>
internal static class FrameProtocol
{
    public const string ProtocolVersion = "V1";

    /// <summary>帧内字段消毒：路径/名字里的制表符与换行替换为空格，避免破坏行协议。</summary>
    public static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    public static string Open(int pid, string name, bool isDir, string path)
        => $"OP {pid}\t{Sanitize(name)}\t{(isDir ? '1' : '0')}\t{Sanitize(path)}";

    public static string Write(int pid, long bytes, string path)
        => $"WR {pid}\t{bytes}\t{Sanitize(path)}";

    public static string Cleanup(int pid, string path)
        => $"CL {pid}\t{Sanitize(path)}";

    public static string Delete(int pid, string path)
        => $"DL {pid}\t{Sanitize(path)}";

    public static string Rename(int pid, string oldPath)
        => $"RN {pid}\t{Sanitize(oldPath)}";

    public static string Name(int pid, string name, string exePath)
        => $"NM {pid}\t{Sanitize(name)}\t{Sanitize(exePath)}";

    public static string Stats(FrameHealth h)
    {
        // 紧凑 JSON（System.Text.Json 默认无空白/制表符），作为 ST 后单一 token
        var json = JsonSerializer.Serialize(h, HealthJson);
        return $"ST {json}";
    }

    private static readonly JsonSerializerOptions HealthJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 解析一行。结构合法（即便类型未知）返回 true 并填 frame；空行/结构性畸形返回 false。
    /// </summary>
    public static bool TryParse(string? line, out Frame frame)
    {
        frame = new Frame(FrameKind.Unknown);
        if (string.IsNullOrWhiteSpace(line)) return false;
        line = line.Trim();

        if (line == "READY") { frame = new Frame(FrameKind.Ready); return true; }
        if (line == "HB") { frame = new Frame(FrameKind.Heartbeat); return true; }

        int sp = line.IndexOf(' ');
        string token = sp < 0 ? line : line[..sp];
        string rest = sp < 0 ? "" : line[(sp + 1)..];

        switch (token)
        {
            case "ERR":
                frame = new Frame(FrameKind.Error, Message: rest);
                return true;
            case "ST":
                return TryParseStats(rest, out frame);
            case "OP":
            {
                var p = rest.Split('\t');
                if (p.Length != 4 || !int.TryParse(p[0], out int pid) || pid <= 0) return false;
                frame = new Frame(FrameKind.Open, Pid: pid, Name: p[1], IsDir: p[2] == "1", Path: p[3]);
                return frame.Path.Length > 0;
            }
            case "WR":
            {
                var p = rest.Split('\t');
                if (p.Length != 3 || !int.TryParse(p[0], out int pid) || pid <= 0
                    || !long.TryParse(p[1], out long bytes) || bytes < 0) return false;
                frame = new Frame(FrameKind.Write, Pid: pid, Bytes: bytes, Path: p[2]);
                return frame.Path.Length > 0;
            }
            case "CL":
            case "DL":
            case "RN":
            {
                var p = rest.Split('\t');
                if (p.Length != 2 || !int.TryParse(p[0], out int pid) || pid <= 0) return false;
                var kind = token == "CL" ? FrameKind.Cleanup : token == "DL" ? FrameKind.Delete : FrameKind.Rename;
                // RN 的路径语义是"旧路径"，存 OldPath；CL/DL 存 Path
                frame = kind == FrameKind.Rename
                    ? new Frame(kind, Pid: pid, OldPath: p[1])
                    : new Frame(kind, Pid: pid, Path: p[1]);
                return p[1].Length > 0;
            }
            case "NM":
            {
                var p = rest.Split('\t');
                if (p.Length != 3 || !int.TryParse(p[0], out int pid) || pid <= 0) return false;
                frame = new Frame(FrameKind.Name, Pid: pid, Name: p[1], ExePath: p[2]);
                return frame.Name.Length > 0;
            }
            default:
                // 未知帧类型（新版本可能下发）：结构完整，调用方计数忽略
                frame = new Frame(FrameKind.Unknown, Message: line);
                return true;
        }
    }

    private static bool TryParseStats(string json, out Frame frame)
    {
        frame = new Frame(FrameKind.Unknown);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var h = JsonSerializer.Deserialize<FrameHealth>(json, HealthJson);
            if (h == null) return false;
            frame = new Frame(FrameKind.Stats, Health: h);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
