namespace RmsCollector;

/// <summary>
/// Optional, read-only YMConnect job evidence. Does NOT claim access to OEM JOB DIAGNOSIS.
/// Legacy environment fallback when the configuration does not specify job polling cadence.
/// </summary>
internal static class JobMonitorOptions
{
    public static bool EverySample => string.Equals(
        Environment.GetEnvironmentVariable("RMS_JOB_MONITOR_EVERY_SAMPLE"), "1", StringComparison.Ordinal);
}

internal static class JobMonitorEvidence
{
    public static object FromSample(RobotSample sample, bool simulated)
    {
        var job = sample.JobData as IReadOnlyDictionary<string, object?>;
        var state = sample.StateData as IReadOnlyDictionary<string, object?>;
        string? name = (Get(job, "name") as string)?.Trim();
        bool validName = !string.IsNullOrEmpty(name) && !name.Any(char.IsControl)
            && System.Text.Encoding.UTF8.GetByteCount(name) <= 120;
        bool attempted = sample.JobReadAt is not null;
        bool success = attempted && sample.Job.Ok && validName;
        string readStatus = !attempted ? "not_polled"
            : !sample.Job.Ok ? "read_failed" : !validName ? "invalid_job" : "read_ok";

        return ApiPayloads.Map(
            ("schema_version", 2),
            ("source", simulated ? "mock" : "YMConnect.GetExecutingJobInformation"),
            ("task", "Master"),
            ("observed_at", sample.JobReadAt),
            ("status_code", attempted ? sample.Job.Code : null),
            ("status_message", readStatus == "read_failed" ? sample.Job.Error : null),
            ("read_ok", success),
            ("read_status", readStatus),
            ("job_name", success ? name : null),
            ("line", success ? Get(job, "line") : null),
            ("step", success ? Get(job, "step") : null),
            ("speed_override_percent", success ? Get(job, "speed_override_percent") : null),
            ("controller_running", state is not null ? Get(state, "running") : null),
            ("torque_read_ok", sample.Torque.Ok),
            ("oem_job_diagnosis_available", false),
            ("oem_job_diagnosis_note", "Other OEM Job Monitor metrics not exposed by verified collector APIs"));
    }

    private static object? Get(IReadOnlyDictionary<string, object?>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) ? value : null;
}
