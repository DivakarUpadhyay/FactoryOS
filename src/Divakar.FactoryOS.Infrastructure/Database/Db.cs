using System.Data;
using System.Reflection;
using Dapper;
using Divakar.FactoryOS.Application.Abstractions;
using DbUp;
using Microsoft.Data.SqlClient;

namespace Divakar.FactoryOS.Infrastructure.Database;

public sealed class DbOptions
{
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Opens SQL connections with the caller's tenant in SESSION_CONTEXT, which the row-level security policy reads.
/// Pooled connections are reset by SQL Server before reuse, so the context never leaks between requests.
/// </summary>
public sealed class Db(DbOptions options, ICurrentUser me)
{
    /// <summary>A connection limited to the signed-in caller's tenant (or all tenants for a platform operator).</summary>
    public Task<SqlConnection> OpenAsync(CancellationToken ct) =>
        OpenWithContextAsync(me.IsAuthenticated ? me.TenantId : null, me.IsAuthenticated && me.IsSuperAdmin, system: false, ct);

    /// <summary>A connection for the API's own work that has no signed-in user yet: sign-in, token refresh, audit writes.</summary>
    public Task<SqlConnection> OpenSystemAsync(CancellationToken ct) => OpenWithContextAsync(null, false, system: true, ct);

    private async Task<SqlConnection> OpenWithContextAsync(Guid? tenantId, bool superAdmin, bool system, CancellationToken ct)
    {
        var con = new SqlConnection(options.ConnectionString);
        await con.OpenAsync(ct);
        await con.ExecuteAsync(new CommandDefinition("""
            EXEC sp_set_session_context N'TenantId', @tenantId, @read_only = 1;
            EXEC sp_set_session_context N'IsSuperAdmin', @superAdmin, @read_only = 1;
            EXEC sp_set_session_context N'System', @system, @read_only = 1;
            """, new { tenantId, superAdmin, system }, cancellationToken: ct));
        return con;
    }
}

public static class Migrator
{
    /// <summary>Creates the database if needed and runs every migration script not yet applied (in name order).</summary>
    public static void Run(string connectionString)
    {
        EnsureDatabase.For.SqlDatabase(connectionString);
        var result = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.Contains(".Database.Migrations."))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException($"Database migration failed in {result.ErrorScript?.Name}: {result.Error.Message}", result.Error);
        }
    }
}

public static class DapperSetup
{
    private static int done;

    public static void Once()
    {
        if (Interlocked.Exchange(ref done, 1) == 1)
        {
            return;
        }
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);
    }
}
