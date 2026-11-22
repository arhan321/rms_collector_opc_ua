using System.Text.Json;
using RmsCollector;

internal static class MockChecks
{
    public static void Run(Action<bool, string> check, string temp)
    {
        var config = CollectorConfig.Load("config/robots.mock8.conf");
        check(config.SimulationOnly && config.Devices.Count == 8, "eight virtual robots are explicitly simulation-only");
        check(config.Devices.Select(d => d.Id).SequenceEqual(Enumerable.Range(1, 8)) &&
            config.Devices.Select(d => d.Ip).SequenceEqual(Enumerable.Range(101, 8).Select(n => $"192.168.255.{n}")),
            "robot IDs/IPs match the old eight-robot example without old HTTP settings");
        var origin = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        using var reader = new MockReader(config.Devices[0], true, 1000, origin);
        reader.Connect();
        RobotSample Read(long second) => reader.Collect(second, true, true, true);
        bool Flag(RobotSample s, string name) => (bool)((Dictionary<string, object?>)s.StateData!)[name]!;
        long[] Counters(RobotSample s) => s.OperatingReadings.Select(r => r.Seconds!.Value).ToArray();
        int HistoryCount(RobotSample s)
        {
            var json = JsonSerializer.SerializeToElement(s.AlarmHistoryData);
            return json.GetProperty("categories")[0].GetProperty("alarms").GetArrayLength();
        }
        var idle = Read(0);
        check(!Flag(idle, "running") && !Flag(idle, "servo_on") && idle.Pulses.SequenceEqual(config.Devices[0].MockProfile.HomePulses),
            "HOME_IDLE has six home pulses, stopped motion and servo off");
        check(idle.Simulation?.HomeStatus == "AT_HOME" && idle.ActiveAlarmCount == 0, "idle home and empty alarms are valid simulated readings");
        var run = Read(20);
        check(Flag(run, "running") && Flag(run, "servo_on") && !Flag(run, "alarm") && run.Simulation?.HomeStatus == "OUTSIDE_HOME",
            "RUN state, servo and position agree");
        var initialCounters = Counters(idle);
        check(Counters(run).Zip(initialCounters, (a, b) => a - b).SequenceEqual(new long[] { 20, 5, 5, 5 }),
            "one-second telemetry advances counters by elapsed seconds rather than a hard-coded five seconds");
        var hold = Read(80);
        var holdAgain = Read(85);
        check(!Flag(hold, "running") && Flag(hold, "servo_on") && Flag(hold, "hold") && hold.Pulses.SequenceEqual(holdAgain.Pulses),
            "HOLD freezes all axes while servo remains on");
        check(Counters(holdAgain).Zip(Counters(hold), (a, b) => a - b).SequenceEqual(new long[] { 5, 5, 0, 0 }),
            "HOLD advances controller/servo time while playback/moving counters stop");
        var load = Read(100);
        check(load.Simulation?.Phase == "HIGH_LOAD" && Math.Abs(load.Torques[0]) > Math.Abs(run.Torques[0]) * 2,
            "high-load demonstration changes torque while the robot is running");
        var alarm = Read(120);
        var alarmAgain = Read(125);
        check(Flag(alarm, "alarm") && Flag(alarm, "error") && !Flag(alarm, "running") && !Flag(alarm, "servo_on") && alarm.ActiveAlarmCount == 1,
            "alarm phase exposes one active alarm with consistent controller state");
        check(HistoryCount(alarm) == 1 && HistoryCount(alarmAgain) == 1 && SampleJson.Json(alarm.ActiveAlarms) == SampleJson.Json(alarmAgain.ActiveAlarms),
            "polling the same alarm does not invent a new event timestamp");
        var returning = Read(140);
        check(returning.ActiveAlarmCount == 0 && !Flag(returning, "alarm") && HistoryCount(returning) == 1,
            "cleared alarm remains in controller history");
        check(Math.Abs(returning.Pulses[0] - idle.Pulses[0]) < Math.Abs(alarm.Pulses[0] - idle.Pulses[0]),
            "return-to-home moves toward the configured reference");
        var teach = Read(155);
        check((string)((Dictionary<string, object?>)teach.StateData!)["control_mode"]! == "Teach" && teach.Simulation?.HomeStatus == "AT_HOME",
            "TEACH mode is separate from automatic operation");
        var lost = Read(170);
        check(!lost.Position.Ok && !lost.Torque.Ok && !lost.State.Ok && !lost.Job.Ok && !lost.Alarm.Ok && !lost.Io.Ok && lost.ActiveAlarmCount is null &&
            lost.OperatingReadings.All(r => !r.Result.Ok && r.Seconds is null) && lost.Simulation?.HomeStatus == "UNKNOWN",
            "communication loss produces unavailable streams rather than false/zero readings");
        var recovered = Read(176);
        check(recovered.Position.Ok && recovered.State.Ok && recovered.Simulation?.CommunicationAvailable == true, "readings recover after the communication-loss window");
        var fullCycle = Read(180);
        check(Counters(fullCycle).Zip(initialCounters, (a, b) => a - b).SequenceEqual(new long[] { 180, 130, 95, 57 }),
            "full-cycle counters reflect controller, servo, playback and physical movement durations");
        var recurrence = Read(300);
        check(HistoryCount(recurrence) == 2 && SampleJson.Json(alarm.ActiveAlarms) != SampleJson.Json(recurrence.ActiveAlarms),
            "a later recurrence is distinct from repeated polling of the first event");
        check(HistoryCount(Read(100000)) == 20, "simulation history is bounded to twenty newest records per populated category");

        bool monotonic = true;
        long[]? previous = null;
        using var counterReader = new MockReader(config.Devices[0], true, 1000, origin);
        for (int second = 0; second <= 540; second++)
        {
            var s = counterReader.Collect(second, true, true, true);
            if (!s.OperatingReadings.All(r => r.Result.Ok)) continue;
            var current = Counters(s);
            monotonic &= previous is null || current.Zip(previous, (a, b) => a >= b).All(v => v);
            monotonic &= current.SequenceEqual(current.OrderDescending());
            previous = current;
        }
        check(monotonic, "all counters are monotonic and moving <= playback <= servo <= controller across three cycles");
        var samples = config.Devices.Select(d => new MockReader(d, true, 1000, origin).Collect(0, true, true, true)).ToArray();
        check(samples.Select(s => s.Simulation!.Phase).Distinct().Count() >= 6, "eight robots start in independent phases rather than identical cloned states");
        using (var json = JsonDocument.Parse(SampleJson.Snapshot(config.Devices[0], "test", 20, origin.ToString("O"), run, true)))
            check(json.RootElement.GetProperty("controller_ip").GetString() == "192.168.255.101" &&
                json.RootElement.GetProperty("raw_payload").GetProperty("robot_identity").GetProperty("axis_count").GetInt32() == 6 &&
                json.RootElement.GetProperty("raw_payload").GetProperty("simulated").GetBoolean(),
                "snapshot preserves Laravel-compatible identity, axis data and explicit simulation metadata");
        var malformed = Path.Combine(temp, "invalid-profiles.json");
        File.WriteAllText(malformed, "{\"schema_version\":1,\"robots\":[]}");
        bool rejected = false;
        try { MockRobotProfile.Load(malformed, config.Devices); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "profile file cannot silently omit configured robot identities");
    }
}
