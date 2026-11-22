using System.Globalization;
using System.Runtime.InteropServices;
using YMConnect;

namespace RmsCollector;

// Only read APIs are used here. The collector never commands motion, servo power,
// job execution, parameter changes or IO writes.
internal sealed class YmConnectReader(Device device, bool jobEverySample = false) : IRobotReader
{
    private MotomanController? controller;
    public bool Connected => controller is not null;

    public static void CheckSdk()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("REAL requires Windows x64. Mock can run on other platforms.");
        string native = Path.Combine(AppContext.BaseDirectory, "YMConnect.dll");
        if (!File.Exists(native)) throw new FileNotFoundException("YMConnect.dll is missing beside rms_collector_opcua.exe", native);
        try
        {
            nint handle = NativeLibrary.Load(native);
            NativeLibrary.Free(handle);
            _ = typeof(MotomanController).Assembly.GetName();
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("Cannot load the official Windows x64 YMConnect SDK. Install the supported Visual C++ v14 Redistributable x64 and keep the matching YMConnect.dll/YMConnect_CS.dll pair together. MSVC Build Tools are not needed.", e);
        }
    }

    public bool Connect()
    {
        Disconnect();
        var candidate = MotomanController.OpenConnection(device.Ip, out StatusInfo status);
        if (status.StatusCode != 0 || candidate is null)
        {
            candidate?.CloseConnection();
            AppLog.Write($"[YMConnect {device.Ip}] OPEN FAILED code={status.StatusCode}");
            return false;
        }
        controller = candidate;
        AppLog.Write($"[YMConnect] Connected {device.Ip}");
        return true;
    }

    public void Disconnect()
    {
        var previous = controller;
        controller = null;
        previous?.CloseConnection();
    }
    public void Dispose() => Disconnect();
    private static Reading Result(StatusInfo status, string failure, string raw = "") =>
        new(status.StatusCode == 0, status.StatusCode, status.StatusCode == 0 ? "" : failure, status.StatusCode == 0 ? raw : "", DateTime.UtcNow);

    public RobotSample Collect(long sequence, bool extras, bool operating, bool alarms)
    {
        var c = controller ?? throw new InvalidOperationException("Not connected to robot");
        var s = new RobotSample { CollectionStartedAtUtc = DateTime.UtcNow };
        var ps = c.ControlGroup.ReadPositionData(ControlGroupId.R1, CoordinateType.Pulse, 0, 0, out PositionData position);
        s.Position = Result(ps, "YMConnect position read failed");
        if (s.Position.Ok)
        {
            if (position.AxisData is not { Length: >= 6 } || position.AxisData.Take(6).Any(v => !double.IsFinite(v)))
                s.Position = s.Position with { Ok = false, Code = -1, Error = "Invalid position axis data from SDK" };
            else s.Pulses = position.AxisData.Take(6).ToArray();
        }
        var ts = c.ControlGroup.ReadTorqueData(ControlGroupId.R1, out TorqueData torque);
        s.Torque = Result(ts, "YMConnect torque read failed");
        if (s.Torque.Ok)
        {
            if (torque.AxisData is not { Length: >= 6 }) s.Torque = s.Torque with { Ok = false, Code = -1, Error = "Invalid torque axis data from SDK" };
            else s.Torques = torque.AxisData.Take(6).Select(v => (double)v).ToArray();
        }
        // Keep the job observation close to the torque read. These are separate SDK calls.
        if (extras || jobEverySample) ReadJobMonitorForTelemetry(c, s);

        // State must be refreshed at the telemetry rate, not only at extras rate.
        var stateStatus = c.Status.ReadState(out ControllerStateData state);
        s.State = Result(stateStatus, "YMConnect state read failed");
        if (s.State.Ok)
        {
            s.StateData = SampleJson.Map(("known", true), ("running", state.IsRunning), ("servo_on", state.IsServoOn),
                ("alarm", state.IsAlarming), ("hold", state.IsInHold), ("error", state.IsErroring),
                ("control_mode", Enum.IsDefined(state.ControlMode) ? state.ControlMode.ToString() : "Unknown"),
                ("cycle_mode", Enum.IsDefined(state.CycleMode) ? state.CycleMode.ToString() : "Unknown"));
            s.State = s.State with { Raw = SampleJson.Json(s.StateData) };
        }
        if (operating) ReadOperating(c, s);
        if (alarms) ReadAlarms(c, s);
        if (extras) ReadExtras(c, s);
        s.CollectionCompletedAtUtc = DateTime.UtcNow;
        return s;
    }

    private static void ReadJobMonitorForTelemetry(MotomanController c, RobotSample s)
    {
        try
        {
            var status = c.Job.GetExecutingJobInformation(InformTaskNumber.Master, out JobData job);
            s.Job = Result(status, "YMConnect job read failed", status.StatusCode == 0 ? SampleJson.Text(job.Name) : "");
            if (s.Job.Ok)
            {
                s.JobData = SampleJson.Map(("name", SampleJson.Text(job.Name)),
                    ("line", job.Line), ("step", job.StepNumber),
                    ("speed_override_percent", job.SpeedOverride), ("task", "Master"));
            }
        }
        catch (Exception exception) when (OpcUaRunner.IsRecoverable(exception))
        {
            s.Job = new(false, -1, $"YMConnect job read failed ({exception.GetType().Name})", SampledAtUtc: DateTime.UtcNow);
            s.JobData = null;
        }
        finally { s.JobReadAt = (s.Job.SampledAtUtc ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture); }
    }

    private static void ReadOperating(MotomanController c, RobotSample s)
    {
        foreach (TimeType type in new[] { TimeType.ControllerOnTime, TimeType.ServoPowerOnTimeRobot, TimeType.PlayBackTimeRobot, TimeType.MovingTimeRobot })
        {
            var status = c.Status.ReadOperatingTimes(ControlGroupId.R1, type, out TimeData data, ApplicationNumber.ApplicationOne);
            Reading result = Result(status, "Operating time read failed");
            long? seconds = null;
            if (result.Ok)
            {
                var elapsed = data.ElapsedTime;
                if (elapsed.Minutes >= 60 || elapsed.Seconds >= 60) result = result with { Ok = false, Code = -1, Error = "YMConnect elapsed time components are out of range" };
                else
                {
                    seconds = checked((long)elapsed.Hours * 3600 + elapsed.Minutes * 60L + elapsed.Seconds);
                    result = result with { Raw = FormattableString.Invariant($"Elapsed Time: {elapsed.Hours} Hours : {elapsed.Minutes} Minutes : {elapsed.Seconds} Seconds") };
                }
            }
            s.OperatingReadings.Add(new(type.ToString(), result, seconds));
        }
    }

    private static object AlarmObject(AlarmData alarm) => SampleJson.Map(
        ("alarm_code", alarm.Code.ToString(CultureInfo.InvariantCulture)), ("subcode", alarm.Subcode.ToString(CultureInfo.InvariantCulture)),
        ("alarm_name", SampleJson.Text(alarm.Name)), ("controller_time_raw", SampleJson.Text(alarm.Time)));

    private static void ReadAlarms(MotomanController c, RobotSample s)
    {
        var status = c.Faults.GetActiveAlarms(out ActiveAlarms active);
        s.Alarm = Result(status, "Active alarm read failed");
        if (s.Alarm.Ok)
        {
            if (active.Count < 0 || active.Count > (active.Alarms?.Length ?? 0))
                s.Alarm = s.Alarm with { Ok = false, Code = -1, Error = "Invalid active alarm count from SDK" };
            else
            {
                s.ActiveAlarmCount = active.Count;
                s.ActiveAlarms = (active.Alarms ?? []).Take(active.Count).Select(AlarmObject).ToArray();
                string text = active.Count == 0 ? "No active alarms." : $"{active.Count} alarm{(active.Count == 1 ? "" : "s")} active.";
                foreach (var (a, i) in (active.Alarms ?? []).Take(active.Count).Select((a, i) => (a, i)))
                    text += $"\nAlarm {i + 1}: Code: {a.Code}\nSubcode: {a.Subcode}\nName: {SampleJson.Text(a.Name)}\nTime: {SampleJson.Text(a.Time)}";
                s.Alarm = s.Alarm with { Raw = text };
            }
        }
        var categories = new List<object>();
        bool allOk = true, anyOk = false;
        foreach (AlarmCategory category in Enum.GetValues<AlarmCategory>())
        {
            var result = c.Faults.GetAlarmHistory(category, 20, out AlarmHistory history);
            bool ok = result.StatusCode == 0;
            int count = ok ? Math.Min(Math.Max(history.NumberOfAlarmsFetched, 0), history.Alarms?.Length ?? 0) : 0;
            allOk &= ok;
            anyOk |= ok;
            categories.Add(SampleJson.Map(("category", category.ToString()), ("read_ok", ok), ("status_code", result.StatusCode),
                ("requested_count", 20), ("fetched_count", ok ? count : null),
                ("alarms", ok ? (history.Alarms ?? []).Take(count).Select(AlarmObject).ToArray() : Array.Empty<object>())));
        }
        s.AlarmHistoryData = SampleJson.Map(("requested_per_category", 20), ("categories", categories));
        s.AlarmHistory = new(allOk, allOk ? 0 : -1, allOk ? "" : anyOk ? "Some alarm history categories failed" : "All alarm history category reads failed",
            SampleJson.Json(s.AlarmHistoryData), DateTime.UtcNow);
    }

    private void ReadExtras(MotomanController c, RobotSample s)
    {
        var info = new Dictionary<string, object?>();
        foreach (var (name, id) in new[] { ("robot_r1", SystemInfoId.R1), ("station_s1", SystemInfoId.S1), ("application_a1", SystemInfoId.A1) })
        {
            var status = c.Status.ReadSystemInformation(id, out SystemInfoData data);
            bool ok = status.StatusCode == 0;
            info[name] = SampleJson.Map(("read_ok", ok), ("status_code", status.StatusCode), ("sampled_at", DateTime.UtcNow),
                ("model_name", ok ? SampleJson.Text(data.ModelName) : null), ("software_version", ok ? SampleJson.Text(data.SoftwareVersion) : null));
        }
        s.SystemInfoData = info;
        s.SystemReadAtUtc = DateTime.UtcNow;
        if (device.Io.Length == 0) { s.Io = new(false, -1, "No IO addresses configured", SampledAtUtc: DateTime.UtcNow); return; }
        var values = new Dictionary<string, object?>();
        bool allOk = true;
        foreach (int address in device.Io)
        {
            var status = c.IO.ReadByte((uint)address, out IOByteData value);
            bool ok = status.StatusCode == 0;
            allOk &= ok;
            values[address.ToString(CultureInfo.InvariantCulture)] = SampleJson.Map(("read_ok", ok), ("status_code", status.StatusCode), ("sampled_at", DateTime.UtcNow),
                ("sdk_raw_text", ok ? Convert.ToString(value.ToByte(), 2).PadLeft(8, '0') : null));
        }
        s.IoData = values;
        s.Io = new(allOk, allOk ? 0 : -1, allOk ? "" : "Some IO address reads failed", SampleJson.Json(values), DateTime.UtcNow);
    }
}
