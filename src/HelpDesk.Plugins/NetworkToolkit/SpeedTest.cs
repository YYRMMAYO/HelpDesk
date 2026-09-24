using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;

namespace HelpDesk.Plugins.NetworkToolkit;

/// <summary>测速进度。</summary>
public readonly record struct SpeedTestProgress(string Phase, double Percent, string Detail);

/// <summary>测速结果。</summary>
public sealed class SpeedTestReport
{
    /// <summary>平均延迟（毫秒）。</summary>
    public double? LatencyMs { get; set; }

    /// <summary>抖动：相邻延迟样本的平均绝对差（毫秒）。</summary>
    public double? JitterMs { get; set; }

    /// <summary>延迟来源说明。</summary>
    public string LatencySource { get; set; } = "不可用";

    /// <summary>下行速率（Mbps）。</summary>
    public double? DownloadMbps { get; set; }

    /// <summary>下行测速节点。</summary>
    public string? DownloadEndpoint { get; set; }

    /// <summary>下行节点的可读名称。</summary>
    public string? DownloadEndpointLabel { get; set; }

    /// <summary>本次下行测速实际传输的数据量（字节）。</summary>
    public long DownloadBytes { get; set; }

    /// <summary>上行速率（Mbps）。</summary>
    public double? UploadMbps { get; set; }

    /// <summary>上行测速节点。</summary>
    public string? UploadEndpoint { get; set; }

    /// <summary>给用户看的补充说明。</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>一个测速节点。</summary>
public sealed record SpeedTestEndpoint(string Url, string Label);

/// <summary>
/// 网速检测。
///
/// <para><b>为什么不能「下载一个文件再除以时间」</b></para>
/// <list type="bullet">
/// <item>TCP 慢启动会让开头几百毫秒的速率明显偏低，直接平均会系统性低估带宽。
/// 所以测量窗口从预热结束后才开始计时，预热阶段的数据只下载、不计入。</item>
/// <item>测速节点普遍对单次请求的响应体有上限（Cloudflare 的 <c>__down</c> 超过
/// 约 64 MB 就直接返回 403），所以固定大小的单次下载在快网络上根本填不满时间窗口。
/// 这里改为「在窗口时间内反复请求同一个节点」，既不受单次上限影响，也不受网速快慢影响。</item>
/// </list>
///
/// <para><b>多节点自动回退</b></para>
/// <para>
/// 先对每个候选节点做一次只读 16 KB 的小请求测首字节时间，再对「首字节最快」的两个
/// 节点各做一次约 2 秒的短吞吐采样，最后在采样最快的那个节点上做完整的窗口测量。
/// </para>
/// <para>
/// 为什么不只用首字节时间挑节点：首字节快只说明「握手近」，不代表「带宽大」。
/// 跨运营商/跨境的链路上常见「响应快但吞吐差」的节点，只看首字节会挑错节点、
/// 把用户的带宽测低一大截。多花约 4 秒和十几 MB 流量换一个可信的数字，是值得的。
/// </para>
/// </summary>
public static class SpeedTestService
{
    /// <summary>预热时长：这段时间的数据不计入速率（毫秒）。</summary>
    private const int WarmupMs = 1200;

    /// <summary>测量窗口时长（毫秒）。</summary>
    private const int WindowMs = 5000;

    /// <summary>单次测速的数据量上限，避免千兆网络下把流量打爆。</summary>
    private const long MaxBytes = 150L * 1024 * 1024;

    /// <summary>单个节点的整体时间上限（毫秒）。</summary>
    private const int EndpointTimeoutMs = 25000;

    /// <summary>挑选节点时「短吞吐采样」的预热时长（毫秒）。</summary>
    private const int SampleWarmupMs = 600;

    /// <summary>挑选节点时「短吞吐采样」的测量时长（毫秒）。</summary>
    private const int SampleWindowMs = 1500;

    /// <summary>挑选节点时「短吞吐采样」的数据量上限。</summary>
    private const long SampleMaxBytes = 24L * 1024 * 1024;

    /// <summary>小于这个数据量认为测不准，换下一个节点。</summary>
    private const long MinViableBytes = 512 * 1024;

    /// <summary>上行预热的字节数。</summary>
    private const int UploadWarmupBytes = 2 * 1024 * 1024;

    /// <summary>上行计时的字节数。</summary>
    private const int UploadMeasureBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 下行测速节点候选。
    /// <para>Cloudflare 的 <c>__down</c> 接受 bytes 参数但上限约 64 MB，超过会返回 403；
    /// 各家 Linux 镜像站的 <c>ls-lR.gz</c> 是长期存在、体积约 38 MB 的固定文件，
    /// 适合作为国内可达的备选节点。</para>
    /// </summary>
    public static readonly SpeedTestEndpoint[] DefaultEndpoints =
    [
        new("https://speed.cloudflare.com/__down?bytes=67108864", "Cloudflare"),
        new("https://mirrors.aliyun.com/ubuntu/ls-lR.gz", "阿里云镜像"),
        new("https://mirrors.tuna.tsinghua.edu.cn/ubuntu/ls-lR.gz", "清华 TUNA 镜像"),
        new("https://mirror.nju.edu.cn/ubuntu/ls-lR.gz", "南京大学镜像")
    ];

    /// <summary>上行端点候选。</summary>
    public static readonly SpeedTestEndpoint[] DefaultUploadEndpoints =
    [
        new("https://speed.cloudflare.com/__up", "Cloudflare")
    ];

    /// <summary>延迟测试目标：公共 DNS，通常不屏蔽 Ping，且离用户较近。</summary>
    private static readonly string[] PingTargets = ["223.5.5.5", "119.29.29.29", "1.1.1.1", "8.8.8.8"];

    public static async Task<SpeedTestReport> RunAsync(
        string? customDownloadUrl,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken ct)
    {
        var report = new SpeedTestReport();

        progress?.Report(new SpeedTestProgress("测试延迟", 3, "正在测试网络延迟…"));
        await MeasureLatencyAsync(report, ct);

        progress?.Report(new SpeedTestProgress("选择节点", 20, "正在寻找合适的测速节点…"));
        var candidates = await SelectEndpointsAsync(customDownloadUrl, report, progress, ct);

        progress?.Report(new SpeedTestProgress("测试下载", 30, "正在测试下载速度（约 7 秒）…"));
        await MeasureDownloadAsync(candidates, report, progress, ct);

        progress?.Report(new SpeedTestProgress("测试上传", 78, "正在测试上传速度（约 10 秒）…"));
        await MeasureUploadAsync(report, progress, ct);

        report.Notes.Add("测速结果受测速节点距离、时段和局域网内其他设备占用影响，仅供参考。");
        report.Notes.Add("测速期间请关闭正在下载或看视频的程序，结果会更接近真实带宽。");
        if (report.DownloadBytes > 0)
            report.Notes.Add($"本次测速共下载约 {report.DownloadBytes / 1024.0 / 1024.0:F0} MB 数据。");

        progress?.Report(new SpeedTestProgress("完成", 100, "测速完成"));
        return report;
    }

    // ══════════════════════════════════════════════════ 延迟 / 抖动

    private static async Task MeasureLatencyAsync(SpeedTestReport report, CancellationToken ct)
    {
        foreach (var target in PingTargets)
        {
            ct.ThrowIfCancellationRequested();
            var samples = new List<long>();

            try
            {
                using var ping = new Ping();
                // 第一个包常常因为 ARP/链路唤醒而偏高，先丢掉
                try { await ping.SendPingAsync(target, 1500); } catch { }

                for (var i = 0; i < 6; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var reply = await ping.SendPingAsync(target, 1500);
                    if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime);
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (samples.Count < 3) continue;

            report.LatencyMs = samples.Average();
            report.JitterMs = MeanAbsoluteDelta(samples);
            report.LatencySource = $"Ping {target}（{samples.Count} 个样本，最小 {samples.Min()} / 最大 {samples.Max()} ms）";
            return;
        }

        report.Notes.Add("所有测试节点都没有响应 Ping（部分网络会屏蔽 Ping），延迟改用下载节点的响应时间估算。");
    }

    /// <summary>抖动：相邻样本差值的平均绝对值，比标准差更贴近「网络一卡一卡」的直观含义。</summary>
    private static double MeanAbsoluteDelta(List<long> samples)
    {
        if (samples.Count < 2) return 0;
        double sum = 0;
        for (var i = 1; i < samples.Count; i++) sum += Math.Abs(samples[i] - samples[i - 1]);
        return sum / (samples.Count - 1);
    }

    // ══════════════════════════════════════════════════ 节点筛选

    /// <summary>
    /// 挑选测速节点：先按首字节时间筛出可达节点，再对最快的两个做短吞吐采样，
    /// 返回按「实测吞吐量」排序的候选列表（第一个是主测节点，其余作为回退）。
    /// </summary>
    private static async Task<List<SpeedTestEndpoint>> SelectEndpointsAsync(
        string? customDownloadUrl,
        SpeedTestReport report,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken ct)
    {
        var candidates = new List<SpeedTestEndpoint>();
        if (!string.IsNullOrWhiteSpace(customDownloadUrl))
            candidates.Add(new SpeedTestEndpoint(customDownloadUrl.Trim(), "自定义地址"));
        candidates.AddRange(DefaultEndpoints);

        using var http = CreateClient();
        var reachable = new List<(SpeedTestEndpoint Endpoint, double LatencyMs)>();

        foreach (var endpoint in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var latency = await MeasureTimeToFirstByteAsync(http, endpoint.Url, ct);
                if (latency != null) reachable.Add((endpoint, latency.Value));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (reachable.Count == 0)
        {
            report.Notes.Add("没有找到可用的测速节点：当前网络可能无法访问这些测速服务器，或被防火墙拦截。" +
                             "可以在「自定义测速地址」里填入你信任的、能直接下载的较大文件地址。");
            return [];
        }

        var byLatency = reachable.OrderBy(r => r.LatencyMs).ToList();

        // 探测结果写进说明：测速不准时用户能一眼看出是不是节点选得不合适
        report.Notes.Add("可用测速节点（响应时间）：" + string.Join("；",
            byLatency.Select(r => $"{r.Endpoint.Label} {r.LatencyMs:F0} ms")));

        if (byLatency.Count == 1) return [byLatency[0].Endpoint];

        // 只对首字节最快的两个做短吞吐采样，兼顾准确性与流量消耗
        var sampled = new List<(SpeedTestEndpoint Endpoint, double Mbps)>();
        foreach (var (endpoint, _) in byLatency.Take(2))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SpeedTestProgress("选择节点", 24, $"正在比较节点速度（{endpoint.Label}）…"));
            try
            {
                var mbps = await SampleThroughputAsync(http, endpoint, ct);
                if (mbps != null) sampled.Add((endpoint, mbps.Value));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (sampled.Count == 0)
            return byLatency.Select(r => r.Endpoint).ToList();

        var byThroughput = sampled.OrderByDescending(s => s.Mbps).ToList();
        report.Notes.Add("节点速度抽样：" + string.Join("；",
            byThroughput.Select(s => $"{s.Endpoint.Label} {s.Mbps:F1} Mbps")));

        progress?.Report(new SpeedTestProgress("选择节点", 27,
            $"选用「{byThroughput[0].Endpoint.Label}」进行完整测速…"));

        // 采样最快者排第一，其余节点（含未采样的）按响应时间排在后面作为回退
        var ordered = byThroughput.Select(s => s.Endpoint).ToList();
        foreach (var (endpoint, _) in byLatency)
            if (!ordered.Contains(endpoint))
                ordered.Add(endpoint);

        return ordered;
    }

    /// <summary>
    /// 短吞吐采样：做一次很短的窗口测量，用于在几个节点之间挑出真正快的那一个。
    /// </summary>
    private static async Task<double?> SampleThroughputAsync(
        HttpClient http,
        SpeedTestEndpoint endpoint,
        CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        var overall = Stopwatch.StartNew();

        long totalBytes = 0;
        long bytesAtWindowStart = 0;
        double? windowStartMs = null;
        var finished = false;

        while (!finished)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await http.GetAsync(endpoint.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) break;

                totalBytes += read;
                var elapsed = overall.Elapsed.TotalMilliseconds;

                if (windowStartMs == null && elapsed >= SampleWarmupMs)
                {
                    windowStartMs = elapsed;
                    bytesAtWindowStart = totalBytes;
                }

                if (totalBytes >= SampleMaxBytes) { finished = true; break; }
                if (windowStartMs is { } start && elapsed - start >= SampleWindowMs) { finished = true; break; }
            }
        }

        overall.Stop();
        if (windowStartMs is not { } windowStart) return null;

        return Rate(totalBytes - bytesAtWindowStart, overall.Elapsed.TotalMilliseconds - windowStart);
    }

    private static async Task<double?> MeasureTimeToFirstByteAsync(
        HttpClient http,
        string url,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        var read = await stream.ReadAsync(buffer, ct);
        stopwatch.Stop();

        return read > 0 ? stopwatch.Elapsed.TotalMilliseconds : null;
    }

    // ══════════════════════════════════════════════════ 下行

    private static async Task MeasureDownloadAsync(
        List<SpeedTestEndpoint> endpoints,
        SpeedTestReport report,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken ct)
    {
        if (endpoints.Count == 0) return;

        using var http = CreateClient();

        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var measurement = await MeasureEndpointAsync(http, endpoint, report, progress, ct);
                if (measurement is null) continue;

                report.DownloadMbps = measurement.Value.Mbps;
                report.DownloadBytes = measurement.Value.Bytes;
                report.DownloadEndpoint = endpoint.Url;
                report.DownloadEndpointLabel = endpoint.Label;

                // Ping 全被屏蔽时，用节点首字节时间作为延迟的近似值
                if (report.LatencyMs == null && measurement.Value.FirstByteMs is > 0)
                {
                    report.LatencyMs = measurement.Value.FirstByteMs;
                    report.LatencySource = "下载节点首字节时间（本网络屏蔽 Ping，改用网页响应时间估算）";
                }

                if (measurement.Value.MeasuredWindowMs < WindowMs * 0.5)
                    report.Notes.Add("本网络速度很快，测速窗口被数据量上限提前结束，结果可能略有偏差。");

                if (!ReferenceEquals(endpoint, endpoints[0]))
                    report.Notes.Add($"首个节点不可用，已自动切换到「{endpoint.Label}」。");

                return;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 换下一个节点
            }
        }

        report.Notes.Add("选中的测速节点在传输过程中失败，可以换一个自定义测速地址再试。");
    }

    private readonly record struct DownloadMeasurement(double Mbps, long Bytes, long MeasuredWindowMs, double? FirstByteMs);

    /// <summary>
    /// 在时间窗口内反复请求同一个节点，测量稳定阶段的吞吐量。
    /// </summary>
    private static async Task<DownloadMeasurement?> MeasureEndpointAsync(
        HttpClient http,
        SpeedTestEndpoint endpoint,
        SpeedTestReport report,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        var overall = Stopwatch.StartNew();

        long totalBytes = 0;
        long bytesAtWarmupEnd = 0;
        double? warmupEndMs = null;
        double? firstByteMs = null;
        var finished = false;
        var gotAnyData = false;
        double lastReportMs = 0;

        while (!finished)
        {
            ct.ThrowIfCancellationRequested();
            if (overall.ElapsedMilliseconds > EndpointTimeoutMs) break;

            using var response = await http.GetAsync(endpoint.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) break;

                totalBytes += read;
                gotAnyData = true;

                var elapsed = overall.Elapsed.TotalMilliseconds;
                firstByteMs ??= elapsed;

                // 预热阶段：只下载不计时（丢弃 TCP 慢启动）
                if (warmupEndMs == null && elapsed >= WarmupMs)
                {
                    warmupEndMs = elapsed;
                    bytesAtWarmupEnd = totalBytes;
                }

                if (totalBytes >= MaxBytes || overall.ElapsedMilliseconds > EndpointTimeoutMs)
                {
                    finished = true;
                    break;
                }

                if (warmupEndMs is { } windowStart)
                {
                    if (elapsed - windowStart >= WindowMs)
                    {
                        finished = true;
                        break;
                    }

                    if (elapsed - lastReportMs >= 300)
                    {
                        lastReportMs = elapsed;
                        var liveMbps = Rate(totalBytes - bytesAtWarmupEnd, elapsed - windowStart);
                        if (liveMbps is { } live)
                            progress?.Report(new SpeedTestProgress("测试下载",
                                30 + Math.Min(45, (elapsed - windowStart) * 45.0 / WindowMs),
                                $"当前下载速度约 {live:F1} Mbps（节点：{endpoint.Label}）…"));
                    }
                }
            }
        }

        overall.Stop();
        if (!gotAnyData) return null;

        var totalMs = overall.Elapsed.TotalMilliseconds;
        long measuredBytes;
        double measuredMs;

        if (warmupEndMs is { } start)
        {
            measuredBytes = totalBytes - bytesAtWarmupEnd;
            measuredMs = totalMs - start;
        }
        else
        {
            // 整个传输比预热时间还短（内网千兆这种），只能用全程
            measuredBytes = totalBytes;
            measuredMs = totalMs;
        }

        if (totalBytes < MinViableBytes) return null;

        var mbps = Rate(measuredBytes, measuredMs);
        if (mbps == null) return null;

        return new DownloadMeasurement(mbps.Value, totalBytes, (long)measuredMs, firstByteMs);
    }

    /// <summary>由字节数与毫秒数换算 Mbps；样本太少时返回 null（避免除出噪声）。</summary>
    private static double? Rate(long bytes, double milliseconds)
    {
        if (bytes <= 0 || milliseconds < 200) return null;
        return bytes * 8.0 / (milliseconds / 1000.0) / 1_000_000.0;
    }

    // ══════════════════════════════════════════════════ 上行

    private static async Task MeasureUploadAsync(
        SpeedTestReport report,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken ct)
    {
        using var http = CreateClient();

        foreach (var endpoint in DefaultUploadEndpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new SpeedTestProgress("测试上传", 80, "正在预热上传通道…"));

                // 预热：只发不测，让 TLS 握手与慢启动先结束
                using (var warmupResponse = await http.PostAsync(
                           endpoint.Url, new ByteArrayContent(new byte[UploadWarmupBytes]), ct))
                {
                    if (!warmupResponse.IsSuccessStatusCode) continue;
                }

                ct.ThrowIfCancellationRequested();
                progress?.Report(new SpeedTestProgress("测试上传", 88, "正在测试上传速度…"));

                var payload = new byte[UploadMeasureBytes];
                var stopwatch = Stopwatch.StartNew();
                using var response = await http.PostAsync(endpoint.Url, new ByteArrayContent(payload), ct);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode) continue;

                var seconds = stopwatch.Elapsed.TotalSeconds;
                if (seconds <= 0.2) continue;

                report.UploadMbps = payload.Length * 8.0 / seconds / 1_000_000.0;
                report.UploadEndpoint = endpoint.Url;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // 换下一个端点
            }
        }

        report.Notes.Add("上行测速不可用（测速服务器不接受上传，或当前网络限制了上行）。");
    }

    // ══════════════════════════════════════════════════ 工具

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 关掉自动解压：否则压缩后的字节数与真实链路流量对不上
            AutomaticDecompression = DecompressionMethods.None
        };

        var http = new HttpClient(handler)
        {
            // 超时统一由 CancellationToken 控制，避免 HttpClient 自己的超时打断长下载
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpDesk-SpeedTest/1.1");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return http;
    }
}
