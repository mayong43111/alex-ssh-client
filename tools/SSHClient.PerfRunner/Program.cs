using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;
using SSHClient.Core.Models;
using SSHClient.Core.Proxy;
using SSHClient.Core.Services;

var options = PerfRunnerOptions.Parse(args);
var runner = new PerfRunner(options);
await runner.RunAsync();

internal sealed class PerfRunner
{
    private readonly PerfRunnerOptions _options;

    public PerfRunner(PerfRunnerOptions options)
    {
        _options = options;
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_options.OutputDirectory);

        Console.WriteLine($"[Perf] Output: {_options.OutputDirectory}");
        Console.WriteLine($"[Perf] Scenario duration: {_options.ScenarioDuration.TotalSeconds:F0}s");

        var report = new PerfReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Runtime = _options,
        };

        report.RuleEngineBaseline = RunRuleEngineBaseline();
        report.ConnectionBaseline = await RunConnectionLatencyBaselineAsync(_options.ConnectionBaselineIterations);
        report.LoadScenarios = await RunLoadScenariosAsync();

        var stamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(_options.OutputDirectory, $"perf-report-{stamp}.json");
        var mdPath = Path.Combine(_options.OutputDirectory, $"perf-report-{stamp}.md");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json);
        await File.WriteAllTextAsync(mdPath, report.ToMarkdown());

        Console.WriteLine($"[Perf] JSON report: {jsonPath}");
        Console.WriteLine($"[Perf] Markdown report: {mdPath}");
    }

    private static RuleEngineBaselineResult RunRuleEngineBaseline()
    {
        var rules = BuildRules(220);
        var engine = new RuleEngine(rules);
        var hosts = new[]
        {
            "api.example.com",
            "service.internal.local",
            "10.8.12.34",
            "cdn.contoso.net",
            "www.doodles.google",
        };

        for (var i = 0; i < 10_000; i++)
        {
            _ = engine.Match(hosts[i % hosts.Length], 443);
        }

        var gc0Start = GC.CollectionCount(0);
        var gc1Start = GC.CollectionCount(1);
        var gc2Start = GC.CollectionCount(2);
        var memBefore = GC.GetTotalMemory(forceFullCollection: false);

        const int total = 500_000;
        var matched = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            if (engine.Match(hosts[i % hosts.Length], 443) is not null)
            {
                matched++;
            }
        }
        sw.Stop();

        var memAfter = GC.GetTotalMemory(forceFullCollection: false);

        return new RuleEngineBaselineResult
        {
            TotalMatches = total,
            Matched = matched,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            ThroughputOpsPerSec = total / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
            AllocatedDeltaBytes = memAfter - memBefore,
            Gen0Collections = GC.CollectionCount(0) - gc0Start,
            Gen1Collections = GC.CollectionCount(1) - gc1Start,
            Gen2Collections = GC.CollectionCount(2) - gc2Start,
        };
    }

    private async Task<ConnectionBaselineResult> RunConnectionLatencyBaselineAsync(int iterations)
    {
        await using var server = await TargetServer.StartAsync();
        var rules = new RuleEngine(new[]
        {
            new ProxyRuleEx { Name = "AllDirect", Pattern = "*", Action = RuleAction.Direct, Type = RuleMatchType.All }
        });

        var proxyManager = new NoopProxyManager();
        var proxyConnector = new UnusedProxyConnector();
        var logger = Log.Logger;

        var latencies = new List<double>(iterations);

        var gc0Start = GC.CollectionCount(0);
        var gc1Start = GC.CollectionCount(1);
        var gc2Start = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            using var client = await UpstreamRouteConnector.ConnectAsync(
                protocol: "PERF-BASELINE",
                host: IPAddress.Loopback.ToString(),
                port: server.Port,
                rules: rules,
                proxyManager: proxyManager,
                proxyConnector: proxyConnector,
                logger: logger,
                preferredProfile: null,
                routeProfileName: null,
                cancellationToken: CancellationToken.None);

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            latencies.Add(elapsedMs);
        }
        sw.Stop();

        latencies.Sort();

        return new ConnectionBaselineResult
        {
            Iterations = iterations,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            ThroughputOpsPerSec = iterations / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
            P50Ms = Percentile(latencies, 0.50),
            P95Ms = Percentile(latencies, 0.95),
            P99Ms = Percentile(latencies, 0.99),
            Gen0Collections = GC.CollectionCount(0) - gc0Start,
            Gen1Collections = GC.CollectionCount(1) - gc1Start,
            Gen2Collections = GC.CollectionCount(2) - gc2Start,
        };
    }

    private async Task<IReadOnlyList<LoadScenarioResult>> RunLoadScenariosAsync()
    {
        var results = new List<LoadScenarioResult>();
        foreach (var concurrency in _options.ConcurrencyScenarios)
        {
            Console.WriteLine($"[Perf] Scenario starting: concurrency={concurrency}");
            var result = await RunSingleScenarioAsync(concurrency, _options.ScenarioDuration);
            results.Add(result);
            Console.WriteLine(
                $"[Perf] Scenario done: concurrency={concurrency}, success={result.SuccessCount}, errors={result.ErrorCount}, p95={result.P95Ms:F2}ms, throughput={result.ThroughputOpsPerSec:F2} ops/s");
        }

        return results;
    }

    private async Task<LoadScenarioResult> RunSingleScenarioAsync(int concurrency, TimeSpan duration)
    {
        await using var server = await TargetServer.StartAsync();
        var rules = new RuleEngine(new[]
        {
            new ProxyRuleEx { Name = "AllDirect", Pattern = "*", Action = RuleAction.Direct, Type = RuleMatchType.All }
        });

        var proxyManager = new NoopProxyManager();
        var proxyConnector = new UnusedProxyConnector();
        var logger = Log.Logger;

        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;

        var gc0Start = GC.CollectionCount(0);
        var gc1Start = GC.CollectionCount(1);
        var gc2Start = GC.CollectionCount(2);

        long success = 0;
        long errors = 0;
        double peakWorkingSetMb = process.WorkingSet64 / 1024d / 1024d;
        double peakManagedMb = GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d;

        var latencySamples = new ConcurrentQueue<double>();
        const int maxSamples = 500_000;
        long sampleCount = 0;

        using var cts = new CancellationTokenSource(duration);
        using var sampleTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        var sampler = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && await sampleTimer.WaitForNextTickAsync(cts.Token))
            {
                process.Refresh();
                peakWorkingSetMb = Math.Max(peakWorkingSetMb, process.WorkingSet64 / 1024d / 1024d);
                peakManagedMb = Math.Max(peakManagedMb, GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d);
            }
        });

        var wall = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        using var client = await UpstreamRouteConnector.ConnectAsync(
                            protocol: "PERF-LOAD",
                            host: IPAddress.Loopback.ToString(),
                            port: server.Port,
                            rules: rules,
                            proxyManager: proxyManager,
                            proxyConnector: proxyConnector,
                            logger: logger,
                            preferredProfile: null,
                            routeProfileName: null,
                            cancellationToken: cts.Token);

                        Interlocked.Increment(ref success);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                    finally
                    {
                        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        if (Interlocked.Increment(ref sampleCount) <= maxSamples)
                        {
                            latencySamples.Enqueue(elapsedMs);
                        }
                    }
                }
            }, cts.Token))
            .ToArray();

        await Task.WhenAll(workers);
        wall.Stop();

        try
        {
            await sampler;
        }
        catch (OperationCanceledException)
        {
        }

        process.Refresh();
        peakWorkingSetMb = Math.Max(peakWorkingSetMb, process.WorkingSet64 / 1024d / 1024d);
        peakManagedMb = Math.Max(peakManagedMb, GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d);

        var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        var avgCpuPercent = cpuMs / Math.Max(wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount, 1) * 100d;

        var total = success + errors;
        var latencyList = latencySamples.ToList();
        latencyList.Sort();

        return new LoadScenarioResult
        {
            Concurrency = concurrency,
            DurationSeconds = wall.Elapsed.TotalSeconds,
            TotalRequests = total,
            SuccessCount = success,
            ErrorCount = errors,
            SuccessRate = total == 0 ? 0 : success * 1.0 / total,
            ErrorRate = total == 0 ? 0 : errors * 1.0 / total,
            ThroughputOpsPerSec = total / Math.Max(wall.Elapsed.TotalSeconds, 0.001),
            P50Ms = Percentile(latencyList, 0.50),
            P95Ms = Percentile(latencyList, 0.95),
            P99Ms = Percentile(latencyList, 0.99),
            AvgCpuPercent = avgCpuPercent,
            PeakWorkingSetMb = peakWorkingSetMb,
            PeakManagedMb = peakManagedMb,
            Gen0Collections = GC.CollectionCount(0) - gc0Start,
            Gen1Collections = GC.CollectionCount(1) - gc1Start,
            Gen2Collections = GC.CollectionCount(2) - gc2Start,
        };
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        rank = Math.Clamp(rank, 0, sorted.Count - 1);
        return sorted[rank];
    }

    private static IReadOnlyList<ProxyRuleEx> BuildRules(int count)
    {
        var list = new List<ProxyRuleEx>(count + 1);
        for (var i = 0; i < count; i++)
        {
            list.Add(new ProxyRuleEx
            {
                Name = $"Rule-{i}",
                Type = (i % 3) switch
                {
                    0 => RuleMatchType.DomainSuffix,
                    1 => RuleMatchType.IpCidr,
                    _ => RuleMatchType.DomainSuffix,
                },
                Pattern = (i % 3) switch
                {
                    0 => "*.example.com",
                    1 => "10.0.0.0/8",
                    _ => "*.contoso.net",
                },
                Action = RuleAction.Proxy,
            });
        }

        list.Add(new ProxyRuleEx
        {
            Name = "Fallback",
            Type = RuleMatchType.All,
            Pattern = "*",
            Action = RuleAction.Direct,
        });

        return list;
    }
}

internal sealed class TargetServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    private TargetServer(TcpListener listener, CancellationTokenSource cts, Task loop)
    {
        _listener = listener;
        _cts = cts;
        _loop = loop;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<TargetServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 8192);
        var cts = new CancellationTokenSource();

        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient? incoming = null;
                try
                {
                    incoming = await listener.AcceptTcpClientAsync(cts.Token);
                    incoming.Dispose();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                finally
                {
                    incoming?.Dispose();
                }
            }
        }, cts.Token);

        return Task.FromResult(new TargetServer(listener, cts, loop));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}

internal sealed class NoopProxyManager : IProxyManager
{
    public Task<bool> ConnectAsync(string profileName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> ConnectAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task DisconnectAsync(string profileName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ProxyProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ProxyProfile>>(Array.Empty<ProxyProfile>());

    public Task ReloadAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class UnusedProxyConnector : IProxyConnector
{
    public Task<TcpClient> ConnectAsync(ProxyProfile profile, string host, int port, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Proxy connector is not expected in direct-route perf runner.");
}

internal sealed class PerfRunnerOptions
{
    public TimeSpan ScenarioDuration { get; init; } = TimeSpan.FromMinutes(10);
    public int ConnectionBaselineIterations { get; init; } = 2_000;
    public string OutputDirectory { get; init; } = Path.Combine("artifacts", "perf");
    public int[] ConcurrencyScenarios { get; init; } = new[] { 1, 10, 50, 100 };

    public static PerfRunnerOptions Parse(string[] args)
    {
        var durationSeconds = 600;
        var iterations = 2_000;
        var output = Path.Combine("artifacts", "perf");
        var scenarios = new[] { 1, 10, 50, 100 };

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quick":
                    durationSeconds = 20;
                    iterations = 500;
                    break;
                case "--duration-seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ds):
                    durationSeconds = ds;
                    i++;
                    break;
                case "--iterations" when i + 1 < args.Length && int.TryParse(args[i + 1], out var it):
                    iterations = it;
                    i++;
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[i + 1];
                    i++;
                    break;
                case "--scenarios" when i + 1 < args.Length:
                    scenarios = args[i + 1]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => int.TryParse(x.Trim(), out var n) ? n : 0)
                        .Where(x => x > 0)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();
                    if (scenarios.Length == 0)
                    {
                        scenarios = new[] { 1, 10, 50, 100 };
                    }
                    i++;
                    break;
            }
        }

        return new PerfRunnerOptions
        {
            ScenarioDuration = TimeSpan.FromSeconds(Math.Max(durationSeconds, 1)),
            ConnectionBaselineIterations = Math.Max(iterations, 100),
            OutputDirectory = output,
            ConcurrencyScenarios = scenarios,
        };
    }
}

internal sealed class PerfReport
{
    public DateTime GeneratedAtUtc { get; set; }
    public PerfRunnerOptions Runtime { get; set; } = new();
    public RuleEngineBaselineResult RuleEngineBaseline { get; set; } = new();
    public ConnectionBaselineResult ConnectionBaseline { get; set; } = new();
    public IReadOnlyList<LoadScenarioResult> LoadScenarios { get; set; } = Array.Empty<LoadScenarioResult>();

    public string ToMarkdown()
    {
        var lines = new List<string>
        {
            "# SSH Client Performance Report",
            string.Empty,
            $"- GeneratedAtUtc: {GeneratedAtUtc:O}",
            $"- ScenarioDurationSeconds: {Runtime.ScenarioDuration.TotalSeconds:F0}",
            $"- ConnectionBaselineIterations: {Runtime.ConnectionBaselineIterations}",
            $"- Scenarios: {string.Join(",", Runtime.ConcurrencyScenarios)}",
            string.Empty,
            "## RuleEngine Baseline",
            string.Empty,
            "| Metric | Value |",
            "|---|---:|",
            $"| Throughput (ops/s) | {RuleEngineBaseline.ThroughputOpsPerSec:F2} |",
            $"| Total Matches | {RuleEngineBaseline.TotalMatches} |",
            $"| Matched | {RuleEngineBaseline.Matched} |",
            $"| Elapsed (ms) | {RuleEngineBaseline.ElapsedMs:F2} |",
            $"| Allocated Delta (bytes) | {RuleEngineBaseline.AllocatedDeltaBytes} |",
            $"| Gen0 / Gen1 / Gen2 | {RuleEngineBaseline.Gen0Collections} / {RuleEngineBaseline.Gen1Collections} / {RuleEngineBaseline.Gen2Collections} |",
            string.Empty,
            "## Connection Baseline (Direct)",
            string.Empty,
            "| Metric | Value |",
            "|---|---:|",
            $"| Throughput (ops/s) | {ConnectionBaseline.ThroughputOpsPerSec:F2} |",
            $"| P50 (ms) | {ConnectionBaseline.P50Ms:F3} |",
            $"| P95 (ms) | {ConnectionBaseline.P95Ms:F3} |",
            $"| P99 (ms) | {ConnectionBaseline.P99Ms:F3} |",
            $"| Gen0 / Gen1 / Gen2 | {ConnectionBaseline.Gen0Collections} / {ConnectionBaseline.Gen1Collections} / {ConnectionBaseline.Gen2Collections} |",
            string.Empty,
            "## Load Scenarios",
            string.Empty,
            "| Concurrency | Duration(s) | Total | Success | Error | SuccessRate | ErrorRate | Throughput(ops/s) | P50(ms) | P95(ms) | P99(ms) | CPU(%) | PeakWS(MB) | PeakManaged(MB) | GC(0/1/2) |",
            "|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
        };

        foreach (var s in LoadScenarios.OrderBy(x => x.Concurrency))
        {
            lines.Add(
                $"| {s.Concurrency} | {s.DurationSeconds:F1} | {s.TotalRequests} | {s.SuccessCount} | {s.ErrorCount} | {s.SuccessRate:P2} | {s.ErrorRate:P2} | {s.ThroughputOpsPerSec:F2} | {s.P50Ms:F3} | {s.P95Ms:F3} | {s.P99Ms:F3} | {s.AvgCpuPercent:F2} | {s.PeakWorkingSetMb:F2} | {s.PeakManagedMb:F2} | {s.Gen0Collections}/{s.Gen1Collections}/{s.Gen2Collections} |");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

internal sealed class RuleEngineBaselineResult
{
    public int TotalMatches { get; set; }
    public int Matched { get; set; }
    public double ElapsedMs { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public long AllocatedDeltaBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

internal sealed class ConnectionBaselineResult
{
    public int Iterations { get; set; }
    public double ElapsedMs { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

internal sealed class LoadScenarioResult
{
    public int Concurrency { get; set; }
    public double DurationSeconds { get; set; }
    public long TotalRequests { get; set; }
    public long SuccessCount { get; set; }
    public long ErrorCount { get; set; }
    public double SuccessRate { get; set; }
    public double ErrorRate { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double AvgCpuPercent { get; set; }
    public double PeakWorkingSetMb { get; set; }
    public double PeakManagedMb { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}
