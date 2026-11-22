using System.Globalization;

namespace RmsCollector;

internal sealed record Device(int Id, string Ip, string Group, int[] Io)
{
    public MockRobotProfile MockProfile { get; set; } = MockRobotProfile.Default(Id);
}

internal sealed class CollectorConfig
{
    public int TelemetryMs { get; private set; } = 5000;
    public int OperatingSeconds { get; private set; } = 60;
    public int AlarmSeconds { get; private set; } = 30;
    public int ExtrasSeconds { get; private set; } = 10;
    public bool JobEverySample { get; private set; } = true;
    public bool SimulationOnly { get; private set; }
    public List<Device> Devices { get; } = [];

    internal static int Integer(string key, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw new InvalidDataException($"Invalid {key}: {value}");
        return n;
    }

    public static CollectorConfig Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Missing config: {path}");
        var c = new CollectorConfig();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        string? simulationProfiles = null;
        int lineNumber = 0;
        foreach (string source in File.ReadLines(path))
        {
            lineNumber++;
            string line = source.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int equal = line.IndexOf('=');
            if (equal <= 0) throw new InvalidDataException($"Expected key=value at line {lineNumber}");
            string key = line[..equal].Trim(), value = line[(equal + 1)..].Trim();
            if (key != "robot" && !seenKeys.Add(key)) throw new InvalidDataException($"Duplicate config key: {key}");
            switch (key)
            {
                case "telemetry_ms": c.TelemetryMs = Integer(key, value, 1000, 3600000); break;
                case "operating_every_s": c.OperatingSeconds = Integer(key, value, 5, 86400); break;
                case "alarms_every_s": c.AlarmSeconds = Integer(key, value, 5, 86400); break;
                case "extras_every_s": c.ExtrasSeconds = Integer(key, value, 5, 86400); break;
                case "job_monitor_every_sample": c.JobEverySample = Integer(key, value, 0, 1) == 1; break;
                case "simulation_only": c.SimulationOnly = Integer(key, value, 0, 1) == 1; break;
                case "simulation_profiles": simulationProfiles = value; break;
                case "robot": c.Devices.Add(ParseDevice(value)); break;
                default: throw new InvalidDataException($"Unknown config key: {key}");
            }
        }
        if (c.Devices.Count is < 1 or > 8) throw new InvalidDataException("Configure 1 to 8 robots for this pilot; validate controller load before scaling");
        if (c.Devices.Select(d => d.Id).Distinct().Count() != c.Devices.Count ||
            c.Devices.Select(d => d.Ip).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Devices.Count)
            throw new InvalidDataException("Duplicate robot ID/IP");
        if (simulationProfiles is not null)
        {
            if (!c.SimulationOnly) throw new InvalidDataException("simulation_profiles requires simulation_only=1");
            MockRobotProfile.Load(simulationProfiles, c.Devices);
        }
        return c;
    }

    private static Device ParseDevice(string value)
    {
        string[] parts = value.Split(',').Select(s => s.Trim()).ToArray();
        if (parts.Length is < 2 or > 4) throw new InvalidDataException("robot requires id,ip[,group[,io;io]]");
        int id = Integer("robot_id", parts[0], 1, 1000000);
        if (parts[1].Length == 0) throw new InvalidDataException("Robot IP is empty");
        string group = parts.Length >= 3 ? parts[2] : "R1";
        if (group != "R1") throw new InvalidDataException("This pilot supports only control group R1");
        int[] io = parts.Length == 4 && parts[3].Length > 0
            ? parts[3].Split(';').Select(s => Integer("IO address", s.Trim(), 1, 99999)).ToArray() : [];
        if (io.Length > 24 || io.Distinct().Count() != io.Length) throw new InvalidDataException("Use at most 24 unique IO byte addresses per robot");
        return new(id, parts[1], group, io);
    }
}
