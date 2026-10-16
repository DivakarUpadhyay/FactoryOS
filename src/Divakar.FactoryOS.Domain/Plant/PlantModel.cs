namespace Divakar.FactoryOS.Domain.Plant;

/// <summary>ISA-95 equipment hierarchy levels, top to bottom.</summary>
public static class NodeLevels
{
    public const string Site = "Site";
    public const string Plant = "Plant";
    public const string Area = "Area";
    public const string Line = "Line";
    public const string Cell = "Cell";

    public static readonly IReadOnlyList<string> Ordered = [Site, Plant, Area, Line, Cell];

    /// <summary>True when <paramref name="childLevel"/> sits directly below <paramref name="parentLevel"/>; a Site has no parent.</summary>
    public static bool IsValidChild(string? parentLevel, string childLevel)
    {
        var child = Ordered.ToList().IndexOf(childLevel);
        if (child < 0)
        {
            return false;
        }
        if (parentLevel is null)
        {
            return child == 0;
        }
        return Ordered.ToList().IndexOf(parentLevel) == child - 1;
    }
}

public static class AssetStatuses
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { "Offline", "Idle", "Running", "Stopped", "Fault", "Maintenance" };
}

public sealed record PlantNode(
    Guid NodeId, Guid? ParentId, string Level, string Code, string Name, string? Description,
    decimal? Latitude, decimal? Longitude, int? LayoutX, int? LayoutY, bool IsActive);

public sealed record Asset(
    Guid AssetId, Guid NodeId, string AssetTypeCode, string? ProtocolCode, string Code, string Name,
    string? Vendor, string? Model, string? SerialNo, string? Endpoint, decimal? RatedPowerKw, decimal? IdealCycleSec,
    string Status, DateOnly? InstalledOn, int? LayoutX, int? LayoutY, bool IsActive);

public sealed record AssetType(string AssetTypeCode, string Category, string Name, string Icon);

public sealed record Protocol(string ProtocolCode, string Family, string Name, string Transport, int? DefaultPort, string? Notes, string SupportLevel);
