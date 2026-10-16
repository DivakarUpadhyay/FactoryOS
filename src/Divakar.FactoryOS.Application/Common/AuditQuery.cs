using Divakar.FactoryOS.Application.Abstractions;

namespace Divakar.FactoryOS.Application.Common;

public sealed record AuditEntry(long AuditId, Guid? TenantId, Guid? UserId, string? UserEmail, string Action, string Entity,
    string? EntityId, string? Detail, string? IpAddress, DateTime At);

public interface IAuditReader
{
    /// <summary>Newest first. Row-level security limits tenant users to their own entries.</summary>
    Task<IReadOnlyList<AuditEntry>> ReadAsync(Guid? tenantId, int take, long? beforeId, CancellationToken ct);
}

public sealed class AuditQuery(IAuditReader reader, ICurrentUser me)
{
    public Task<IReadOnlyList<AuditEntry>> ReadAsync(int take, long? beforeId, CancellationToken ct) =>
        reader.ReadAsync(me.IsSuperAdmin ? null : me.RequireTenant(), Math.Clamp(take, 1, 500), beforeId, ct);
}
