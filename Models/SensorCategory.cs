namespace Pulse.Models;

public enum SensorCategory
{
    CPU,
    GPU,
    Memory,
    Network,
    Storage,
    System
}

/// One entry in the settings GPU picker. Id is the LibreHardwareMonitor identifier,
/// or empty string for the automatic option.
public class GpuChoice
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
}
