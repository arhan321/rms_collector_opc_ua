using System.Diagnostics;
using System.Text;

namespace RmsCollector;

internal static class AppLog
{
    private static readonly object Sync = new();
    public static void Write(string text) { lock (Sync) Console.WriteLine(text); }
}

public static class OpcUaProgram
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
    internal static bool IsRecoverable(Exception error) => error is IOException or TimeoutException
        or InvalidOperationException or System.Net.Sockets.SocketException
        or System.Runtime.InteropServices.ExternalException;

    private static void CloseReader(IRobotReader reader, Device device, bool dispose = false)
    {
        try { if (dispose) reader.Dispose(); else reader.Disconnect(); }
        catch (Exception error) when (IsRecoverable(error))
        {
            AppLog.Write($"[ROBOT id={device.Id} ip={device.Ip}] cleanup failed: {error.GetType().Name}: {error.Message}");
        }
    }

    public static async Task RunAsync(CollectorConfig config, Device device, RmsNodeManager nodes, bool mock,
        CancellationToken cancellation, Func<IRobotReader>? factory = null)
    {
        var reader = factory?.Invoke() ?? (mock ? (IRobotReader)new MockReader(device, config.JobEverySample, config.TelemetryMs) : new YmConnectReader(device, config.JobEverySample));
        string session = Guid.NewGuid().ToString();
        long sequence = 0;
        int badReads = 0, retryAttempt = 0;
        string? lastSimulationPhase = null;
        var clock = Stopwatch.StartNew();
        TimeSpan nextOperating = TimeSpan.Zero, nextAlarms = TimeSpan.Zero, nextExtras = TimeSpan.Zero;
        async Task Retry(string reason)
        {
            nodes.Disconnect(device.Id, reason);
            CloseReader(reader, device);
            badReads = 0;
            nextOperating = nextAlarms = nextExtras = TimeSpan.Zero;
            double milliseconds = Math.Min(30000, Math.Min(config.TelemetryMs, 5000) * Math.Pow(2, Math.Min(retryAttempt++, 5)));
            AppLog.Write($"[ROBOT id={device.Id} ip={device.Ip}] {reason}; reconnect in {milliseconds / 1000:0.#}s");
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellation);
        }
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TimeSpan start = clock.Elapsed;
                RobotSample sample;
                bool operating, alarms, extras;
                // Only connection/reader failures are recoverable here. Publisher and
                // configuration/invariant errors remain fatal and visible to the host.
                try
                {
                    if (!reader.Connected)
                    {
                        if (!reader.Connect())
                        {
                            await Retry("Robot connection failed");
                            continue;
                        }
                        nextOperating = nextAlarms = nextExtras = TimeSpan.Zero;
                    }
                    operating = start >= nextOperating;
                    alarms = start >= nextAlarms;
                    extras = start >= nextExtras;
                    sample = reader.Collect(sequence, extras, operating, alarms);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception error) when (IsRecoverable(error))
                {
                    await Retry($"Reader failure ({error.GetType().Name}): {error.Message}");
                    continue;
                }
                if (mock && sample.Simulation is { } simulation && simulation.Phase != lastSimulationPhase)
                {
                    AppLog.Write($"[SIM robot={device.Id} ip={device.Ip}] phase={simulation.Phase} home={simulation.HomeStatus}");
                    lastSimulationPhase = simulation.Phase;
                }
                nodes.Publish(device, sample, session, sequence++, DateTime.UtcNow, operating, alarms, extras);
                if (sample.Position.Ok || sample.Torque.Ok)
                {
                    badReads = 0;
                    retryAttempt = 0;
                }
                else if (++badReads >= 3)
                {
                    await Retry("Three consecutive telemetry failures");
                    continue;
                }
                if (operating) nextOperating = start + TimeSpan.FromSeconds(config.OperatingSeconds);
                if (alarms) nextAlarms = start + TimeSpan.FromSeconds(config.AlarmSeconds);
                if (extras) nextExtras = start + TimeSpan.FromSeconds(config.ExtrasSeconds);
                TimeSpan delay = start + TimeSpan.FromMilliseconds(config.TelemetryMs) - clock.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            nodes.Disconnect(device.Id, "Collector stopped");
            CloseReader(reader, device, dispose: true);
        }
    }
}
