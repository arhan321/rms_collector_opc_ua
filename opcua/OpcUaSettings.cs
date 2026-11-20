namespace RmsCollector;

internal sealed record OpcUaSettings(CollectorConfig Robots, Uri Endpoint, string PkiDirectory)
{
    public static OpcUaSettings Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string source in File.ReadLines(path))
        {
            string line = source.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int split = line.IndexOf('=');
            if (split < 1) throw new InvalidDataException("Expected key=value in OPC UA configuration");
            string key = line[..split].Trim(), value = line[(split + 1)..].Trim();
            if (key is not ("robot_config" or "endpoint" or "pki_dir") || !values.TryAdd(key, value) || value.Length == 0)
                throw new InvalidDataException($"Unknown, duplicate or empty OPC UA configuration key: {key}");
        }
        if (!values.TryGetValue("robot_config", out string? robotConfig)) throw new InvalidDataException("robot_config is required");
        if (!values.TryGetValue("endpoint", out string? address) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "opc.tcp" ||
            endpoint.Port is < 1 or > 65535 || endpoint.Host is "0.0.0.0" or "::" ||
            !string.IsNullOrEmpty(endpoint.Query + endpoint.Fragment + endpoint.UserInfo))
            throw new InvalidDataException("endpoint must be an opc.tcp://collector-host:port/path URL");
        return new(CollectorConfig.Load(robotConfig), endpoint, Path.GetFullPath(values.GetValueOrDefault("pki_dir", "./pki-opcua")));
    }
}
