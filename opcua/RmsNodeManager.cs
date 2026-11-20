using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace RmsCollector;

internal sealed class RmsNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:adm:rms:ymconnect:opcua";
    private static readonly string[] Axes = ["S", "L", "U", "R", "B", "T"];
    private static readonly string[] Counters = ["ControllerOnTime", "ServoPowerOnTimeRobot", "PlayBackTimeRobot", "MovingTimeRobot"];
    private readonly CollectorConfig config;
    private readonly bool mock;
    private readonly Dictionary<string, BaseDataVariableState> values = [];
    private readonly Dictionary<(int Robot, string Stream), DateTime> observed = [];
    private readonly Dictionary<string, int> ttls;

    public RmsNodeManager(IServerInternal server, ApplicationConfiguration configuration, CollectorConfig config, bool mock)
        : base(server, configuration, server.Telemetry.CreateLogger<RmsNodeManager>(), NamespaceUri)
    {
        this.config = config;
        this.mock = mock;
        int telemetryTtl = Math.Max(5, (int)Math.Ceiling(config.TelemetryMs / 1000d) * 2 + 5);
        ttls = new Dictionary<string, int>
        {
            ["Telemetry"] = telemetryTtl, ["Status"] = telemetryTtl,
            ["Job"] = config.JobEverySample ? telemetryTtl : config.ExtrasSeconds * 2 + 5,
            ["Operating"] = config.OperatingSeconds * 2 + 5,
            ["Alarms"] = config.AlarmSeconds * 2 + 5,
            ["IO"] = config.ExtrasSeconds * 2 + 5,
            ["System"] = config.ExtrasSeconds * 2 + 5
        };
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();
            var root = Folder(null, "RMS", "RMS");
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            refs.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
            var robots = Folder(root, "RMS/Robots", "Robots");
            foreach (var device in config.Devices) CreateRobot(robots, device);
            AddPredefinedNode(SystemContext, root);
        }
    }

    private void CreateRobot(NodeState parent, Device device)
    {
        string path = $"RMS/Robots/{device.Id}";
        var robot = Folder(parent, path, device.Id.ToString(CultureInfo.InvariantCulture));
        Static(robot, path + "/RobotId", DataTypeIds.Int32, device.Id);
        Static(robot, path + "/IpAddress", DataTypeIds.String, device.Ip);
        Static(robot, path + "/Group", DataTypeIds.String, device.Group);
        Static(robot, path + "/Simulated", DataTypeIds.Boolean, mock);
        Static(robot, path + "/Connected", DataTypeIds.Boolean, false);
        Static(robot, path + "/HasFreshTelemetry", DataTypeIds.Boolean, false);
        Static(robot, path + "/ConnectionMessage", DataTypeIds.String, "Waiting for robot data");

        var telemetry = Stream(robot, path, "Telemetry");
        Variable(telemetry, path + "/Telemetry/SessionId", DataTypeIds.String);
        Variable(telemetry, path + "/Telemetry/Sequence", DataTypeIds.Int64);
        Variable(telemetry, path + "/Telemetry/SnapshotJson", DataTypeIds.String);
        var torque = Folder(telemetry, path + "/Telemetry/Torque", "Torque");
        var pulse = Folder(telemetry, path + "/Telemetry/Pulse", "Pulse");
        Static(torque, path + "/Telemetry/Torque/Unit", DataTypeIds.String, "sdk_raw");
        Static(pulse, path + "/Telemetry/Pulse/Unit", DataTypeIds.String, "pulses");
        foreach (string axis in Axes)
        {
            Variable(torque, path + "/Telemetry/Torque/" + axis, DataTypeIds.Double);
            Variable(pulse, path + "/Telemetry/Pulse/" + axis, DataTypeIds.Double);
        }
        foreach (var (kind, folder) in new[] { ("Pulse", pulse), ("Torque", torque) })
        {
            Variable(folder, path + "/Telemetry/" + kind + "/ReadOk", DataTypeIds.Boolean);
            Variable(folder, path + "/Telemetry/" + kind + "/StatusCode", DataTypeIds.Int32);
        }
        var status = Stream(robot, path, "Status");
        foreach (string name in new[] { "Running", "ServoOn", "Alarming", "Hold", "Error" })
            Variable(status, path + "/Status/" + name, DataTypeIds.Boolean);
        foreach (string name in new[] { "ControlMode", "CycleMode" })
            Variable(status, path + "/Status/" + name, DataTypeIds.String);
        var job = Stream(robot, path, "Job");
        Variable(job, path + "/Job/Name", DataTypeIds.String);
        foreach (string name in new[] { "Line", "Step" }) Variable(job, path + "/Job/" + name, DataTypeIds.Int32);
        Variable(job, path + "/Job/SpeedOverridePercent", DataTypeIds.Double);
        Variable(job, path + "/Job/ReadAtUtc", DataTypeIds.DateTime);
        Variable(job, path + "/Job/ReadStatus", DataTypeIds.String);
        Static(job, path + "/Job/Task", DataTypeIds.String, "Master");
        Static(job, path + "/Job/OemDiagnosisAvailable", DataTypeIds.Boolean, false);
        var operating = Stream(robot, path, "Operating");
        foreach (string type in Counters)
        {
            var counter = Folder(operating, path + "/Operating/" + type, type);
            Variable(counter, path + "/Operating/" + type + "/Seconds", DataTypeIds.Int64);
            Variable(counter, path + "/Operating/" + type + "/Hours", DataTypeIds.Double);
            Variable(counter, path + "/Operating/" + type + "/ReadOk", DataTypeIds.Boolean);
        }
        var alarms = Stream(robot, path, "Alarms");
        Variable(alarms, path + "/Alarms/ActiveCount", DataTypeIds.Int32);
        Variable(alarms, path + "/Alarms/ActiveJson", DataTypeIds.String);
        Variable(alarms, path + "/Alarms/HistoryJson", DataTypeIds.String);
        Variable(alarms, path + "/Alarms/HistoryReadOk", DataTypeIds.Boolean);
        var io = Stream(robot, path, "IO");
        Variable(io, path + "/IO/Json", DataTypeIds.String);
        foreach (int address in device.Io) Variable(io, path + "/IO/Byte" + address, DataTypeIds.Int32);
        var system = Stream(robot, path, "System");
        Variable(system, path + "/System/Json", DataTypeIds.String);
    }

    private FolderState Stream(NodeState parent, string path, string name)
    {
        var stream = Folder(parent, path + "/" + name, name);
        Variable(stream, path + "/" + name + "/ReadOk", DataTypeIds.Boolean);
        Variable(stream, path + "/" + name + "/SampledAtUtc", DataTypeIds.DateTime);
        return stream;
    }

    private FolderState Folder(NodeState? parent, string path, string name)
    {
        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(path, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name, SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType, WriteMask = AttributeWriteMask.None, UserWriteMask = AttributeWriteMask.None
        };
        parent?.AddChild(folder);
        return folder;
    }

    private void Static(NodeState parent, string path, NodeId type, object value)
    {
        Variable(parent, path, type);
        Set(path, value, StatusCodes.Good, DateTime.UtcNow);
    }

    private void Variable(NodeState parent, string path, NodeId type)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(path, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name, SymbolicName = name, ReferenceTypeId = ReferenceTypeIds.HasComponent,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType, DataType = type, ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead, UserAccessLevel = AccessLevels.CurrentRead,
            WriteMask = AttributeWriteMask.None, UserWriteMask = AttributeWriteMask.None,
            Historizing = false, MinimumSamplingInterval = 500,
            StatusCode = StatusCodes.BadWaitingForInitialData, Timestamp = DateTime.UtcNow
        };
        parent.AddChild(variable);
        values.Add(path, variable);
    }

    private void Set(string path, object? value, uint quality, DateTime at)
    {
        var variable = values[path];
        variable.Value = value;
        variable.StatusCode = quality;
        variable.Timestamp = at;
        variable.ClearChangeMasks(SystemContext, false);
    }

    private uint Quality(bool valid) => !valid ? StatusCodes.BadNoCommunication
        : mock ? StatusCodes.UncertainSubstituteValue : StatusCodes.Good;

    private void Mark(int id, string stream, DateTime at, bool ok)
    {
        string path = $"RMS/Robots/{id}/{stream}";
        observed[(id, stream)] = at;
        Set(path + "/ReadOk", ok, StatusCodes.Good, at);
        Set(path + "/SampledAtUtc", at, StatusCodes.Good, at);
    }

    public void Publish(Device device, RobotSample sample, string session, long sequence, DateTime at, bool operating, bool alarms, bool extras)
    {
        lock (Lock)
        {
            string p = $"RMS/Robots/{device.Id}";
            Set(p + "/Connected", true, StatusCodes.Good, at);
            Set(p + "/ConnectionMessage", mock ? "SIMULATED DATA" : "YMConnect connected", StatusCodes.Good, at);
            bool torqueOk = sample.Torque.Ok && sample.Torques.Length >= 6 && sample.Torques.Take(6).All(double.IsFinite);
            bool pulseOk = sample.Position.Ok && sample.Pulses.Length >= 6 && sample.Pulses.Take(6).All(double.IsFinite);
            Mark(device.Id, "Telemetry", at, torqueOk || pulseOk);
            Set(p + "/HasFreshTelemetry", torqueOk || pulseOk, StatusCodes.Good, at);
            foreach (var (name, read, valid) in new[] { ("Pulse", sample.Position, pulseOk), ("Torque", sample.Torque, torqueOk) })
            {
                Set(p + "/Telemetry/" + name + "/ReadOk", valid, StatusCodes.Good, at);
                Set(p + "/Telemetry/" + name + "/StatusCode", read.Code, StatusCodes.Good, at);
            }
            for (int i = 0; i < Axes.Length; i++)
            {
                Set(p + "/Telemetry/Torque/" + Axes[i], torqueOk ? sample.Torques[i] : null, Quality(torqueOk), at);
                Set(p + "/Telemetry/Pulse/" + Axes[i], pulseOk ? sample.Pulses[i] : null, Quality(pulseOk), at);
            }
            // One self-contained snapshot for consumers needing coherent identity/axes/status.
            if (torqueOk == sample.Torque.Ok && pulseOk == sample.Position.Ok)
                Set(p + "/Telemetry/SnapshotJson", SampleJson.Snapshot(device, session, sequence, at.ToString("O"), sample, mock), Quality(torqueOk || pulseOk), at);
            else Set(p + "/Telemetry/SnapshotJson", null, StatusCodes.BadDecodingError, at);
            Set(p + "/Telemetry/SessionId", session, StatusCodes.Good, at);
            Set(p + "/Telemetry/Sequence", sequence, StatusCodes.Good, at);
            Mark(device.Id, "Status", at, sample.State.Ok);
            var state = JsonSerializer.SerializeToElement(sample.StateData);
            foreach (var (tag, key) in new[] { ("Running", "running"), ("ServoOn", "servo_on"), ("Alarming", "alarm"), ("Hold", "hold"), ("Error", "error") })
                Set(p + "/Status/" + tag, sample.State.Ok ? Bool(state, key) : null, Quality(sample.State.Ok && Bool(state, key) is not null), at);
            foreach (var (tag, key) in new[] { ("ControlMode", "control_mode"), ("CycleMode", "cycle_mode") })
                Set(p + "/Status/" + tag, sample.State.Ok ? Text(state, key) : null, Quality(sample.State.Ok && Text(state, key) is not null), at);

            if (sample.JobReadAt is not null)
            {
                var job = JsonSerializer.SerializeToElement(JobMonitorEvidence.FromSample(sample, mock));
                bool ok = Bool(job, "read_ok") == true;
                DateTime readAt = DateTime.Parse(sample.JobReadAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                Mark(device.Id, "Job", readAt, ok);
                Set(p + "/Job/ReadAtUtc", readAt, StatusCodes.Good, readAt);
                Set(p + "/Job/ReadStatus", Text(job, "read_status"), StatusCodes.Good, readAt);
                Set(p + "/Job/Name", ok ? Text(job, "job_name") : null, Quality(ok), readAt);
                foreach (var (tag, key) in new[] { ("Line", "line"), ("Step", "step") })
                    Set(p + "/Job/" + tag, ok ? Integer(job, key) : null, Quality(ok && Integer(job, key) is not null), readAt);
                Set(p + "/Job/SpeedOverridePercent", ok ? Number(job, "speed_override_percent") : null, Quality(ok && Number(job, "speed_override_percent") is not null), readAt);
            }
            if (operating)
            {
                Mark(device.Id, "Operating", at, sample.OperatingReadings.Count > 0 && sample.OperatingReadings.All(r => r.Result.Ok));
                foreach (string type in Counters)
                {
                    var counter = sample.OperatingReadings.FirstOrDefault(r => r.Type == type);
                    bool ok = counter?.Result.Ok == true && counter.Seconds is >= 0;
                    string path = p + "/Operating/" + type;
                    Set(path + "/ReadOk", ok, StatusCodes.Good, at);
                    Set(path + "/Seconds", ok ? counter!.Seconds : null, Quality(ok), at);
                    Set(path + "/Hours", ok ? counter!.Seconds / 3600d : null, Quality(ok), at);
                }
            }
            if (alarms)
            {
                Mark(device.Id, "Alarms", at, sample.Alarm.Ok);
                Set(p + "/Alarms/ActiveCount", sample.Alarm.Ok ? sample.ActiveAlarmCount : null, Quality(sample.Alarm.Ok && sample.ActiveAlarmCount is not null), at);
                Set(p + "/Alarms/ActiveJson", sample.Alarm.Ok ? SampleJson.Json(sample.ActiveAlarms) : null, Quality(sample.Alarm.Ok), at);
                Set(p + "/Alarms/HistoryReadOk", sample.AlarmHistory.Ok, StatusCodes.Good, at);
                Set(p + "/Alarms/HistoryJson", sample.AlarmHistoryData is null ? null : SampleJson.Json(sample.AlarmHistoryData), Quality(sample.AlarmHistory.Ok), at);
            }
            if (extras)
            {
                Mark(device.Id, "IO", at, sample.Io.Ok);
                Set(p + "/IO/Json", sample.IoData is null ? null : SampleJson.Json(sample.IoData), Quality(sample.Io.Ok), at);
                var io = JsonSerializer.SerializeToElement(sample.IoData);
                foreach (int address in device.Io)
                {
                    int? value = IoByte(io, address);
                    Set(p + "/IO/Byte" + address, value, Quality(value.HasValue), at);
                }
                var system = JsonSerializer.SerializeToElement(sample.SystemInfoData);
                bool systemOk = mock ? sample.SystemInfoData is not null : Bool(Get(system, "robot_r1"), "read_ok") == true;
                Mark(device.Id, "System", at, systemOk);
                Set(p + "/System/Json", sample.SystemInfoData is null ? null : SampleJson.Json(sample.SystemInfoData), Quality(systemOk), at);
            }
        }
    }

    public void Disconnect(int robotId, string message)
    {
        lock (Lock)
        {
            string prefix = $"RMS/Robots/{robotId}/";
            foreach (string stream in ttls.Keys) Invalidate(prefix + stream + "/", StatusCodes.BadNoCommunication);
            Set(prefix + "Connected", false, StatusCodes.Good, DateTime.UtcNow);
            Set(prefix + "HasFreshTelemetry", false, StatusCodes.Good, DateTime.UtcNow);
            Set(prefix + "ConnectionMessage", message, StatusCodes.Good, DateTime.UtcNow);
        }
    }

    public void Expire(DateTime now)
    {
        lock (Lock)
        {
            foreach (var (key, at) in observed)
            {
                if (now - at <= TimeSpan.FromSeconds(ttls[key.Stream])) continue;
                Invalidate($"RMS/Robots/{key.Robot}/{key.Stream}/", StatusCodes.BadNoCommunication);
                if (key.Stream == "Telemetry")
                {
                    Set($"RMS/Robots/{key.Robot}/HasFreshTelemetry", false, StatusCodes.Good, now);
                    Set($"RMS/Robots/{key.Robot}/Connected", false, StatusCodes.Good, now);
                    Set($"RMS/Robots/{key.Robot}/ConnectionMessage", "Telemetry stale", StatusCodes.Good, now);
                }
            }
        }
    }

    private void Invalidate(string prefix, uint quality)
    {
        foreach (var (path, variable) in values)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.EndsWith("/Unit", StringComparison.Ordinal) ||
                path.EndsWith("/Task", StringComparison.Ordinal) || path.EndsWith("/OemDiagnosisAvailable", StringComparison.Ordinal)) continue;
            // Keep the old source timestamp so old data cannot look newly sampled.
            variable.StatusCode = quality;
            variable.ClearChangeMasks(SystemContext, false);
        }
    }

    private static JsonElement Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    private static string? Text(JsonElement value, string key) => Get(value, key) is var item && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool? Bool(JsonElement value, string key) => Get(value, key).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static int? Integer(JsonElement value, string key) => Get(value, key) is var item && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int n) ? n : null;
    private static double? Number(JsonElement value, string key) => Get(value, key) is var item && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double n) && double.IsFinite(n) ? n : null;
    private static int? IoByte(JsonElement io, int address)
    {
        string key = address.ToString(CultureInfo.InvariantCulture);
        var value = Get(io, key);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int direct) && direct is >= 0 and <= 255) return direct;
        if (Bool(value, "read_ok") == true && Text(value, "sdk_raw_text") is string bits && bits.Length == 8 && bits.All(c => c is '0' or '1')) return Convert.ToInt32(bits, 2);
        return null;
    }
}
