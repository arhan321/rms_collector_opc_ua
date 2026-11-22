using System.Diagnostics;
using System.Text;

namespace RmsCollector;

internal static class AppLog
{
    private static readonly object Sync = new();
    public static void Write(string text) { lock (Sync) Console.WriteLine(text); }
}

internal static class OpcUaProgram
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string path = "config/collector.opcua.conf";
            bool mock = false, real = false, checkSdk = false;
            int duration = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value");
                switch (args[i])
                {
                    case "--config": path = Value(); break;
                    case "--mock": mock = true; break;
                    case "--real": real = true; break;
                    case "--check-sdk": checkSdk = true; break;
                    case "--duration": duration = CollectorConfig.Integer("duration", Value(), 1, 86400); break;
                    case "--help":
                        Console.WriteLine("rms_collector_opcua --config config/collector.opcua.conf --real|--mock [--duration seconds]\nrms_collector_opcua --check-sdk\nRead-only OPC UA server. Run from the project/publish folder.");
                        return 0;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            if (checkSdk)
            {
                if (mock || real || duration != 0) throw new ArgumentException("Use --check-sdk separately from collection options");
                YmConnectReader.CheckSdk();
                AppLog.Write("[SDK] Windows x64 YMConnect libraries load successfully. No robot connection was made.");
                return 0;
            }
            if (mock == real) throw new ArgumentException("Choose exactly one of --real or --mock");
            var settings = OpcUaSettings.Load(path);
            if (real && settings.Robots.SimulationOnly)
                throw new InvalidOperationException("This configuration is simulation-only. Use --mock, or use your separate verified real-robot configuration.");
            if (real) YmConnectReader.CheckSdk();
            using var cancellation = new CancellationTokenSource();
            if (duration > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(duration));
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                await using var host = await OpcUaHost.StartAsync(settings, mock);
                AppLog.Write($"[OPCUA] {settings.Endpoint} | namespace={RmsNodeManager.NamespaceUri}");
                AppLog.Write($"[OPCUA] {(mock ? "SIMULATED DATA: not actual robot" : "YMConnect read-only data")} | security=Basic256Sha256/SignAndEncrypt");
                AppLog.Write($"[OPCUA] Trust the Ignition client certificate in {settings.PkiDirectory}/trusted/certs");
                foreach (var device in settings.Robots.Devices)
                    AppLog.Write($"[ROBOT] id={device.Id} ip={device.Ip} group={device.Group} source={(mock ? "SIMULATION" : "YMConnect")}");
                var tasks = settings.Robots.Devices.Select(device => Task.Run(async () =>
                {
                    try { await OpcUaRunner.RunAsync(settings.Robots, device, host.Nodes, mock, cancellation.Token); }
                    catch { cancellation.Cancel(); throw; }
                })).ToArray();
                await Task.WhenAll(tasks);
                return 0;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception e) { Console.Error.WriteLine($"[ERROR] {e.Message}"); return 1; }
    }
}

internal static class OpcUaRunner
{
    public static async Task RunAsync(CollectorConfig config, Device device, RmsNodeManager nodes, bool mock,
        CancellationToken cancellation, Func<IRobotReader>? factory = null)
    {
        using var reader = factory?.Invoke() ?? (mock ? (IRobotReader)new MockReader(device, config.JobEverySample, config.TelemetryMs) : new YmConnectReader(device, config.JobEverySample));
        string session = Guid.NewGuid().ToString();
        long sequence = 0;
        int badReads = 0;
        string? lastSimulationPhase = null;
        var clock = Stopwatch.StartNew();
        TimeSpan nextOperating = TimeSpan.Zero, nextAlarms = TimeSpan.Zero, nextExtras = TimeSpan.Zero;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TimeSpan start = clock.Elapsed;
                if (!reader.Connected && !reader.Connect())
                {
                    nodes.Disconnect(device.Id, "YMConnect connection failed");
                }
                else
                {
                    bool operating = start >= nextOperating, alarms = start >= nextAlarms, extras = start >= nextExtras;
                    var sample = reader.Collect(sequence, extras, operating, alarms);
                    if (mock && sample.Simulation is { } simulation && simulation.Phase != lastSimulationPhase)
                    {
                        AppLog.Write($"[SIM robot={device.Id} ip={device.Ip}] phase={simulation.Phase} home={simulation.HomeStatus}");
                        lastSimulationPhase = simulation.Phase;
                    }
                    nodes.Publish(device, sample, session, sequence++, DateTime.UtcNow, operating, alarms, extras);
                    if (sample.Position.Ok || sample.Torque.Ok) badReads = 0;
                    else if (++badReads >= 3)
                    {
                        nodes.Disconnect(device.Id, "Three consecutive telemetry failures");
                        reader.Disconnect();
                        badReads = 0;
                    }
                    if (operating) nextOperating = start + TimeSpan.FromSeconds(config.OperatingSeconds);
                    if (alarms) nextAlarms = start + TimeSpan.FromSeconds(config.AlarmSeconds);
                    if (extras) nextExtras = start + TimeSpan.FromSeconds(config.ExtrasSeconds);
                }
                TimeSpan delay = start + TimeSpan.FromMilliseconds(config.TelemetryMs) - clock.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { nodes.Disconnect(device.Id, "Collector stopped"); }
    }
}
