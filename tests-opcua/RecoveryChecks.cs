using System.Text.Json;
using Opc.Ua;
using RmsCollector;

internal static class RecoveryChecks
{
    public static async Task Run(Action<bool, string> check, CollectorConfig config, RmsNodeManager nodes,
        Func<string, int, Task<DataValue>> read)
    {
        var device = config.Devices[0];
        using (var reader = new MockReader(device, true, config.TelemetryMs))
        {
            var sample = reader.Collect(20, true, true, true);
            DateTime published = DateTime.UtcNow;
            sample.Position = sample.Position with { SampledAtUtc = published.AddSeconds(-3) };
            sample.Torque = sample.Torque with { SampledAtUtc = published.AddSeconds(-2) };
            sample.State = sample.State with { SampledAtUtc = published.AddSeconds(-1) };
            nodes.Publish(device, sample, "timing-check", 20, published, true, true, true);
            var pulse = await read("Telemetry/Pulse/S", device.Id);
            var torque = await read("Telemetry/Torque/S", device.Id);
            check(Math.Abs((pulse.SourceTimestamp - sample.Position.SampledAtUtc!.Value).TotalMilliseconds) < 1 &&
                Math.Abs((torque.SourceTimestamp - sample.Torque.SampledAtUtc!.Value).TotalMilliseconds) < 1,
                "pulse and torque keep independent source times despite a later publish time");
            using (var json = JsonDocument.Parse((string)(await read("Telemetry/SnapshotJson", device.Id)).Value))
                check(json.RootElement.GetProperty("position_sampled_at").GetDateTime() == sample.Position.SampledAtUtc &&
                    json.RootElement.GetProperty("torque_sampled_at").GetDateTime() == sample.Torque.SampledAtUtc &&
                    !json.RootElement.GetProperty("raw_payload").GetProperty("sampling").GetProperty("atomic").GetBoolean(),
                    "snapshot preserves individual axis times and declares sequential sampling");

            sample.Position = sample.Position with { SampledAtUtc = published.AddMinutes(-1) };
            sample.Torque = sample.Torque with { SampledAtUtc = published };
            nodes.Publish(device, sample, "timing-check", 21, published, true, true, true);
            check(StatusCode.IsBad((await read("Telemetry/Pulse/S", device.Id)).StatusCode) &&
                !StatusCode.IsBad((await read("Telemetry/Torque/S", device.Id)).StatusCode) &&
                (bool)(await read("HasFreshTelemetry", device.Id)).Value,
                "old pulse expires independently while fresh torque remains usable");
            check(StatusCode.IsBad((await read("Telemetry/SnapshotJson", device.Id)).StatusCode),
                "a newly serialized snapshot cannot disguise an expired axis read as fresh");
            sample.Torque = sample.Torque with { SampledAtUtc = published.AddMinutes(-1) };
            nodes.Publish(device, sample, "timing-check", 22, published, true, true, true);
            check(!(bool)(await read("HasFreshTelemetry", device.Id)).Value &&
                !(bool)(await read("Connected", device.Id)).Value,
                "late completion with both axis reads expired is disconnected immediately");
        }

        var recovering = new TestReader(config.Devices[0], failConnect: true, failCollect: true, failDispose: true);
        var healthy = new TestReader(config.Devices[1]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        async Task Worker(Device robot, TestReader source)
        {
            try { await OpcUaRunner.RunAsync(config, robot, nodes, true, cancellation.Token, () => source); }
            catch { cancellation.Cancel(); throw; }
        }
        Task healthyTask = Worker(config.Devices[1], healthy);
        Task recoveringTask = Worker(config.Devices[0], recovering);
        try
        {
            await recovering.Success.Task.WaitAsync(TimeSpan.FromSeconds(9));
            check(!cancellation.IsCancellationRequested && healthy.Calls >= 2 && !healthyTask.IsCompleted,
                "a robot connect/read exception does not cancel a healthy robot worker");
            check(recovering.Connects >= 3 && recovering.Calls >= 2,
                "recoverable connect and read exceptions reconnect the affected robot");
            check(recovering.LastOperating && recovering.LastAlarms && recovering.LastExtras,
                "successful reconnection refreshes operating, alarms and extras immediately");
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(healthyTask, recoveringTask);
        }
        check(healthyTask.IsCompletedSuccessfully && recoveringTask.IsCompletedSuccessfully && recovering.Disposed,
            "cancellation and a recoverable dispose error complete without crashing other workers");
    }

    private sealed class TestReader : IRobotReader
    {
        private readonly MockReader inner;
        private bool failConnect, failCollect;
        private readonly bool failDispose;
        public bool Connected { get; private set; }
        public int Connects, Calls;
        public bool LastOperating, LastAlarms, LastExtras, Disposed;
        public TaskCompletionSource<bool> Success { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TestReader(Device device, bool failConnect = false, bool failCollect = false, bool failDispose = false)
        {
            inner = new MockReader(device, true, 1000);
            this.failConnect = failConnect;
            this.failCollect = failCollect;
            this.failDispose = failDispose;
        }
        public bool Connect()
        {
            Connects++;
            if (failConnect) { failConnect = false; throw new IOException("Injected connect failure"); }
            return Connected = true;
        }
        public void Disconnect() => Connected = false;
        public RobotSample Collect(long sequence, bool extras, bool operating, bool alarms)
        {
            Calls++;
            if (failCollect) { failCollect = false; throw new TimeoutException("Injected read timeout"); }
            LastOperating = operating; LastAlarms = alarms; LastExtras = extras;
            var sample = inner.Collect(sequence, extras, operating, alarms);
            Success.TrySetResult(true);
            return sample;
        }
        public void Dispose()
        {
            Disposed = true;
            Disconnect();
            inner.Dispose();
            if (failDispose) throw new IOException("Injected close failure");
        }
    }
}
