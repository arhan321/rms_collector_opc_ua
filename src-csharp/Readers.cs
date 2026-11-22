namespace RmsCollector;

internal interface IRobotReader : IDisposable
{
    bool Connected { get; }
    bool Connect();
    void Disconnect();
    RobotSample Collect(long sequence, bool extras, bool operating, bool alarms);
}

internal sealed class MockReader : IRobotReader
{
    private readonly Device device;
    private readonly bool jobEverySample;
    private readonly int telemetryMs;
    private readonly DateTimeOffset startedAt;
    private readonly List<object> history = [];
    private long lastAlarmCycle = -1;
    private static readonly string[] CounterNames = ["ControllerOnTime", "ServoPowerOnTimeRobot", "PlayBackTimeRobot", "MovingTimeRobot"];
    private static readonly double[] Travel = [42000, -31000, 27000, 15000, -22000, 36000];

    public MockReader(Device device, bool jobEverySample = false, int telemetryMs = 5000, DateTimeOffset? startedAt = null)
    {
        if (telemetryMs < 1) throw new ArgumentOutOfRangeException(nameof(telemetryMs));
        this.device = device;
        this.jobEverySample = jobEverySample;
        this.telemetryMs = telemetryMs;
        this.startedAt = startedAt ?? DateTimeOffset.UtcNow;
    }
    public bool Connected { get; private set; }
    public bool Connect() => Connected = true;
    public void Disconnect() => Connected = false;
    public void Dispose() => Disconnect();
    public RobotSample Collect(long sequence, bool extras, bool operating, bool alarms)
    {
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        DateTime sampledAt = DateTime.UtcNow;
        var profile = device.MockProfile;
        double elapsed = (double)sequence * telemetryMs / 1000d;
        double absolute = profile.PhaseOffsetSeconds + elapsed;
        double t = absolute % MockTimeline.CycleSeconds;
        long cycle = (long)Math.Floor(absolute / MockTimeline.CycleSeconds);
        string phase = MockTimeline.Phase(t);
        bool available = phase != "COMMUNICATION_LOSS";
        bool running = MockTimeline.Running(t), servo = MockTimeline.ServoOn(t), alarming = phase == "ALARM";
        double position = MockTimeline.Position(t);
        string home = !available ? "UNKNOWN" : position < 1e-10 ? "AT_HOME" : "OUTSIDE_HOME";
        Reading Result(string what) => available ? new(true, 0, "", "SIMULATED " + what, sampledAt)
            : new(false, -1, "SIMULATED communication loss", SampledAtUtc: sampledAt);
        var s = new RobotSample
        {
            Position = Result("pulse"), Torque = Result("torque"), State = Result("state"),
            CollectionStartedAtUtc = sampledAt,
            Simulation = new(phase, t, cycle, available, home, cycle * 3 + (t >= 45 ? 1 : 0) + (t >= 75 ? 1 : 0) + (t >= 115 ? 1 : 0))
        };
        for (int i = 0; i < 6; i++)
        {
            s.Pulses[i] = Math.Round(profile.HomePulses[i] + Travel[i] * position * (1 + (device.Id - 1) * 0.025));
            double load = !servo ? 0 : running ? 12 + i * 2 + 4 * position : 3 + i * 0.7;
            if (phase == "HIGH_LOAD" && i < 2) load += 45 + 4 * Math.Sin(elapsed * 0.4 + device.Id);
            double noise = servo ? 0.35 * Math.Sin(elapsed * 0.7 + device.Id + i) : 0;
            s.Torques[i] = Math.Round((i % 2 == 0 ? 1 : -1) * profile.TorqueScale * (load + noise), 3);
        }
        s.StateData = available ? SampleJson.Map(("known", true), ("servo_on", servo), ("running", running),
            ("alarm", alarming), ("hold", phase == "HOLD"), ("error", alarming),
            ("control_mode", phase == "TEACH" ? "Teach" : "Play"),
            ("cycle_mode", phase == "TEACH" ? "Step" : "Automatic"), ("source", "mock")) : null;

        // Record events even when this particular sample does not poll alarm history.
        long newestAlarmCycle = (long)Math.Floor((absolute - 115) / MockTimeline.CycleSeconds);
        for (long alarmCycle = Math.Max(lastAlarmCycle + 1, newestAlarmCycle - 19); alarmCycle <= newestAlarmCycle; alarmCycle++)
            history.Add(Alarm(alarmCycle));
        lastAlarmCycle = Math.Max(lastAlarmCycle, newestAlarmCycle);
        if (history.Count > 20) history.RemoveRange(0, history.Count - 20);

        if (operating)
        {
            for (int i = 0; i < CounterNames.Length; i++)
            {
                long seconds = checked((long)Math.Round(profile.InitialOperatingHours[i] * 3600) +
                    (long)Math.Floor(MockTimeline.Counter(absolute, i) - MockTimeline.Counter(profile.PhaseOffsetSeconds, i) + 1e-9));
                var result = available ? new Reading(true, 0, "", $"Elapsed Time: {seconds / 3600} Hours : {seconds / 60 % 60} Minutes : {seconds % 60} Seconds", sampledAt) : Result("operating");
                s.OperatingReadings.Add(new(CounterNames[i], result, available ? seconds : null));
            }
        }
        if (alarms)
        {
            s.Alarm = Result("active alarms");
            s.ActiveAlarmCount = available ? alarming ? 1 : 0 : null;
            s.ActiveAlarms = available && alarming ? [Alarm(cycle)] : [];
            s.AlarmHistory = Result("alarm history");
            s.AlarmHistoryData = available ? SampleJson.Map(("requested_per_category", 20),
                ("categories", new[] { "Major", "Minor", "UserSystem", "User", "Offline" }.Select(category =>
                    SampleJson.Map(("category", category), ("read_ok", true), ("status_code", 0), ("requested_count", 20),
                        ("fetched_count", category == "Major" ? history.Count : 0),
                        ("alarms", category == "Major" ? history.AsEnumerable().Reverse().ToArray() : Array.Empty<object>()))).ToArray())) : null;
        }
        if (extras || jobEverySample)
        {
            s.Job = Result("job");
            s.JobReadAt = sampledAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            double jobProgress = t < 45 ? Math.Clamp(t - 15, 0, 30) : Math.Clamp(t - 45, 0, 30);
            s.JobData = available ? SampleJson.Map(("name", profile.Jobs[t < 45 ? 0 : 1]),
                ("line", (int)(jobProgress * 3)), ("step", (int)(jobProgress * 2)),
                ("speed_override_percent", phase == "TEACH" ? 10 : 80 + device.Id % 5 * 5),
                ("task", "Master"), ("source", "mock")) : null;
        }
        if (extras)
        {
            object Info(string model) => SampleJson.Map(("read_ok", true), ("status_code", 0),
                ("model_name", model), ("software_version", "SIMULATED-1.0"));
            s.SystemInfoData = available ? SampleJson.Map(("simulated", true),
                ("robot_r1", Info(profile.Model)), ("station_s1", Info("SIM STATION S1")),
                ("application_a1", Info("SIM APPLICATION A1")), ("controller_model", profile.ControllerModel)) : null;
            s.SystemReadAtUtc = sampledAt;
            s.Io = device.Io.Length == 0 ? new(false, -1, "No IO addresses configured") : Result("IO byte");
            int flags = (running ? 1 : 0) | (servo ? 2 : 0) | (phase == "HOLD" ? 4 : 0) | (alarming ? 8 : 0) |
                (home == "AT_HOME" ? 16 : 0) | (phase == "TEACH" ? 32 : 0) | (phase == "HIGH_LOAD" ? 64 : 0);
            s.IoData = available ? device.Io.ToDictionary(a => a.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => (object)SampleJson.Map(("read_ok", true), ("status_code", 0),
                    ("sdk_raw_text", Convert.ToString(flags, 2).PadLeft(8, '0')))) : null;
        }
        s.CollectionCompletedAtUtc = DateTime.UtcNow;
        return s;
    }

    private object Alarm(long cycle) => SampleJson.Map(
        ("alarm_code", (900001 + (device.Id - 1) % 3).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("subcode", "1"), ("alarm_name", "SIMULATION - DEMO LOAD ALARM (not an OEM diagnosis)"),
        ("controller_time_raw", startedAt.AddSeconds(cycle * MockTimeline.CycleSeconds + 115 - device.MockProfile.PhaseOffsetSeconds).UtcDateTime.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
        ("source", "mock"), ("simulated", true));
}
