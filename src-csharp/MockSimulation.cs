using System.Text.Json;
using System.Text.Json.Serialization;

namespace RmsCollector;

internal sealed class MockRobotProfile
{
    [JsonPropertyName("robot_id")] public int RobotId { get; set; }
    [JsonPropertyName("robot_code")] public string RobotCode { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "SP210";
    [JsonPropertyName("controller_model")] public string ControllerModel { get; set; } = "YRC1000";
    [JsonPropertyName("serial_number")] public string SerialNumber { get; set; } = "";
    [JsonPropertyName("line_name")] public string LineName { get; set; } = "SIM Welding";
    [JsonPropertyName("station_name")] public string StationName { get; set; } = "";
    [JsonPropertyName("home_pulses")] public double[] HomePulses { get; set; } = [152870, -109862, -51918, 598, -257242, 75088];
    [JsonPropertyName("jobs")] public string[] Jobs { get; set; } = ["MASTER", "TRIAL_SPOT"];
    [JsonPropertyName("phase_offset_seconds")] public double PhaseOffsetSeconds { get; set; }
    [JsonPropertyName("torque_scale")] public double TorqueScale { get; set; } = 1;
    [JsonPropertyName("initial_operating_hours")] public double[] InitialOperatingHours { get; set; } = [21445.37, 12978.41, 7113.27, 993.63];

    public static MockRobotProfile Default(int id) => new()
    {
        RobotId = id, RobotCode = $"SIM_R{id:00}", Name = $"SIM Robot {id:00}",
        SerialNumber = $"SIMULATED-{id:000}", StationName = $"SIM Station {id:00}",
        PhaseOffsetSeconds = ((id - 1L) * 22) % MockTimeline.CycleSeconds,
        TorqueScale = 0.85 + ((id - 1) % 8) * 0.05
    };

    public object Identity(Device device) => SampleJson.Map(
        ("robot_id", device.Id), ("robot_code", RobotCode), ("name", Name),
        ("controller_ip", device.Ip), ("model", Model), ("controller_model", ControllerModel),
        ("serial_number", SerialNumber), ("line_name", LineName), ("station_name", StationName),
        ("control_group", device.Group), ("axis_count", 6), ("simulated", true));

    public static void Load(string path, List<Device> devices)
    {
        var file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty simulation profile file");
        if (file.SchemaVersion != 1 || file.Robots is null || file.Robots.Any(p => p is null) || file.Robots.Count != devices.Count ||
            file.Robots.Select(p => p.RobotId).Distinct().Count() != file.Robots.Count ||
            !file.Robots.Select(p => p.RobotId).Order().SequenceEqual(devices.Select(d => d.Id).Order()))
            throw new InvalidDataException("Simulation profile IDs must exactly match the configured robots; schema_version must be 1");
        foreach (var profile in file.Robots)
        {
            bool validText(string value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && System.Text.Encoding.UTF8.GetByteCount(value) <= 120;
            if (!new[] { profile.RobotCode, profile.Name, profile.Model, profile.ControllerModel, profile.SerialNumber, profile.LineName, profile.StationName }.All(validText) ||
                profile.HomePulses is not { Length: 6 } || !profile.HomePulses.All(double.IsFinite) ||
                profile.Jobs is not { Length: >= 2 } || !profile.Jobs.All(validText) ||
                !double.IsFinite(profile.PhaseOffsetSeconds) || profile.PhaseOffsetSeconds is < 0 or >= MockTimeline.CycleSeconds ||
                !double.IsFinite(profile.TorqueScale) || profile.TorqueScale is < 0.1 or > 2 ||
                profile.InitialOperatingHours is not { Length: 4 } || profile.InitialOperatingHours.Any(h => !double.IsFinite(h) || h < 0 || h > 1000000) ||
                !profile.InitialOperatingHours.SequenceEqual(profile.InitialOperatingHours.OrderDescending()))
                throw new InvalidDataException($"Invalid simulation profile for robot {profile.RobotId}: check identity, six axes, jobs, offsets and counter order");
            devices.Single(d => d.Id == profile.RobotId).MockProfile = profile;
        }
    }

    private sealed class ProfileFile
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("robots")] public List<MockRobotProfile> Robots { get; set; } = [];
    }
}

internal sealed record MockObservation(string Phase, double PhaseSecond, long CycleIndex,
    bool CommunicationAvailable, string HomeStatus, long CompletedJobs)
{
    public object Payload() => SampleJson.Map(("source", "deterministic_demo_v1"), ("phase", Phase),
        ("cycle_second", PhaseSecond), ("cycle_index", CycleIndex), ("cycle_duration_seconds", MockTimeline.CycleSeconds),
        ("communication_available", CommunicationAvailable), ("home_status", HomeStatus),
        ("completed_demo_jobs", CompletedJobs), ("alarm_codes_are_synthetic", true));
}

internal static class MockTimeline
{
    public const int CycleSeconds = 180;
    public static string Phase(double t) => t switch
    {
        < 15 => "HOME_IDLE", < 75 => "RUN", < 95 => "HOLD", < 115 => "HIGH_LOAD",
        < 135 => "ALARM", < 150 => "RETURN_HOME", < 165 => "TEACH", < 175 => "COMMUNICATION_LOSS", _ => "HOME_IDLE"
    };
    public static bool Running(double t) => t is >= 15 and < 75 or >= 95 and < 115 or >= 135 and < 150;
    public static bool ServoOn(double t) => t is >= 15 and < 115 or >= 135 and < 165;
    public static double Motion(double elapsed)
    {
        double u = elapsed % 24;
        double Smooth(double x) => (1 - Math.Cos(Math.PI * x)) / 2;
        return u < 6 ? Smooth(u / 6) : u < 12 ? 1 : u < 18 ? 1 - Smooth((u - 12) / 6) : 0;
    }
    public static double Position(double t) => t switch
    {
        < 15 => 0, < 75 => Motion(t - 15), < 95 => Motion(60), < 115 => Motion(60 + t - 95),
        < 135 => Motion(80), < 150 => Motion(80) * (1 + Math.Cos(Math.PI * (t - 135) / 15)) / 2, _ => 0
    };
    private static double Overlap(double t, double start, double end) => Math.Clamp(t - start, 0, end - start);
    private static double MotionSeconds(double t) => Math.Floor(t / 24) * 12 + Overlap(t % 24, 0, 6) + Overlap(t % 24, 12, 18);
    public static double Counter(double absoluteSecond, int type)
    {
        double cycles = Math.Floor(absoluteSecond / CycleSeconds), t = absoluteSecond % CycleSeconds;
        double Partial(double value) => type switch
        {
            0 => value,
            1 => Overlap(value, 15, 115) + Overlap(value, 135, 165),
            2 => Overlap(value, 15, 75) + Overlap(value, 95, 115) + Overlap(value, 135, 150),
            3 => MotionSeconds(Overlap(value, 15, 75)) + MotionSeconds(60 + Overlap(value, 95, 115)) - MotionSeconds(60) + Overlap(value, 135, 150),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        return cycles * Partial(CycleSeconds) + Partial(t);
    }
}
