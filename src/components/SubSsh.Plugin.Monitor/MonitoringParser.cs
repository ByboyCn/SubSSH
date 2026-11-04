// SubSsh.Plugin.Monitor — 采样输出解析：monitor.go parse / parseCPU / parseLoad 的逐行移植。
// 差分 CPU：100 * (1 - idleDelta/totalDelta)；内存：MemTotal - MemAvailable；
// 负载：/proc/loadavg 首字段（行含 "/"）或 uptime "load averages?: N" 正则；
// 磁盘：__SUBSSH_DF__ 之后的 df -P -k 行（fields[1]=total、fields[2]=used、fields[5:]=mount）。
using System.Text.RegularExpressions;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Monitor;

internal static partial class MonitoringParser
{
    // Go: uptimeLoad = regexp.MustCompile(`load averages?:?\s*([0-9]+(?:\.[0-9]+)?)`)
    [GeneratedRegex(@"load averages?:?\s*([0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex UptimeLoad();

    private const string DfMarker = "__SUBSSH_DF__";

    /// <summary>解析一次采样输出。返回快照；newTotal>0 时调用方保存差分基线（对应 Go parse 返回的 cpu.Total）。</summary>
    public static MonitorSnapshot Parse(
        long hostId, string text, (ulong Total, ulong Idle)? prev,
        out (ulong Total, ulong Idle) cpu, out ulong? newTotal)
    {
        cpu = ParseCpu(text);
        newTotal = cpu.Total;

        var snap = new MonitorSnapshot { HostId = hostId, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        // 差分 CPU（Go: if prev != nil && cpu.Total > prev.Total）
        if (prev != null && cpu.Total > prev.Value.Total)
        {
            ulong dt = cpu.Total - prev.Value.Total;
            ulong idle = cpu.Idle > prev.Value.Idle ? cpu.Idle - prev.Value.Idle : 0;
            double v = 100.0 * (1.0 - (double)idle / (double)dt);
            snap = snap with { CpuPct = v };
        }

        // 内存（Go: Sscanf "MemTotal: %d kB" / "MemAvailable: %d kB"）
        long total = 0, avail = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("MemTotal:")) total = SscanKb(line);
            if (line.StartsWith("MemAvailable:")) avail = SscanKb(line);
        }
        if (total > 0)
        {
            snap = snap with { MemTotalKb = total, MemUsedKb = total - avail };
        }

        snap = snap with { Load1 = ParseLoad(text) };

        // df 解析（Go: marker := strings.Index(text, __SUBSSH_DF__); 缺标记报错）
        var marker = text.IndexOf(DfMarker, StringComparison.Ordinal);
        if (marker < 0)
            throw new SubSsh.Core.Abstractions.SubSshException("采样输出缺少 df 标记");

        var disks = new List<MonitorDisk>();
        foreach (var line in text[(marker + DfMarker.Length)..].Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || fields[0] == "Filesystem") continue;
            if (long.TryParse(fields[1], out var totalKb) && long.TryParse(fields[2], out var usedKb))
            {
                disks.Add(new MonitorDisk
                {
                    Mount = string.Join(" ", fields[5..]),
                    UsedKb = usedKb,
                    TotalKb = totalKb,
                });
            }
        }
        return snap with { Disks = disks };
    }

    // Go: parseCPU —— 取第一行首字段为 "cpu" 的行，字段 1..n 累加为 Total；
    // 1-based i==4、i==5 两列累加为 Idle（/proc/stat 的 idle+iowait）。
    public static (ulong Total, ulong Idle) ParseCpu(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 5 && f[0] == "cpu")
            {
                ulong total = 0, idle = 0;
                for (int i = 1; i < f.Length; i++)
                {
                    ulong.TryParse(f[i], out var v);
                    total += v;
                    if (i == 4 || i == 5) idle += v;
                }
                return (total, idle);
            }
        }
        return (0, 0);
    }

    // Go: parseLoad —— ① 字段 >=3 且行含 "/"：解析首字段（/proc/loadavg 格式）；
    // ② uptimeLoad 正则匹配 "load averages?..."（BSD uptime 格式）。两者都取第一个命中行。
    public static double? ParseLoad(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 3 && line.Contains('/'))
            {
                if (double.TryParse(f[0], out var v1)) return v1;
            }
            var m = UptimeLoad().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, out var v2)) return v2;
        }
        return null;
    }

    // Go fmt.Sscanf(line, "Key: %d kB") 的等价实现：冒号后取前导十进制数字。
    private static long SscanKb(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return 0;
        int i = colon + 1;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        int start = i;
        while (i < line.Length && line[i] is >= '0' and <= '9') i++;
        return i > start && long.TryParse(line[start..i], out var v) ? v : 0;
    }
}
