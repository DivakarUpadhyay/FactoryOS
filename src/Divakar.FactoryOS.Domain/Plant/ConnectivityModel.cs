namespace Divakar.FactoryOS.Domain.Plant;

public sealed record SensorType(string SensorTypeCode, string Category, string Name, string Unit, double? MinValue, double? MaxValue, string SimProfile);

public sealed record TagTemplate(string AssetTypeCode, string TagCode, string Name, string SensorTypeCode);

public sealed record Tag(
    Guid TagId, Guid AssetId, string Code, string Name, string SensorTypeCode, string Unit, string? Address,
    double Scale, double Offset, double Deadband, double? MinValue, double? MaxValue, bool IsActive);

/// <summary>Reading quality codes stored with every value.</summary>
public static class Quality
{
    public const byte Good = 0;
    public const byte Uncertain = 1;
    public const byte Bad = 2;
    public const byte Offline = 3;
}

/// <summary>
/// Machine state values carried by tags of sensor type "machine_state" (a simplified PackML state set),
/// and the asset status each one maps to.
/// </summary>
public static class MachineStates
{
    public const int Offline = 0;
    public const int Idle = 1;
    public const int Running = 2;
    public const int Stopped = 3;
    public const int Fault = 4;
    public const int Maintenance = 5;

    public static string ToStatus(double? value) => (int?)value switch
    {
        Idle => "Idle",
        Running => "Running",
        Stopped => "Stopped",
        Fault => "Fault",
        Maintenance => "Maintenance",
        _ => "Offline",
    };
}

public static class SupportLevels
{
    public const string Native = "Native";
    public const string Gateway = "Gateway";
    public const string Push = "Push";
    public const string Planned = "Planned";
}
