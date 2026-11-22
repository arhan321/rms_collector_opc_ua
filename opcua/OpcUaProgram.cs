namespace RmsCollector.Legacy;

// Compatibility entrypoint. All flags, validation, readers and retry policies
// are owned by the root project, so the old project cannot bypass them.
internal static class LegacyEntryPoint
{
    public static Task<int> Main(string[] args) => global::RmsCollector.OpcUaProgram.Main(args);
}
