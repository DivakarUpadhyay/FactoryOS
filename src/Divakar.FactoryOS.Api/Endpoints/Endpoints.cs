using System.Net;
using Divakar.FactoryOS.Api.Security;
using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Alarms;
using Divakar.FactoryOS.Application.Assistant;
using Divakar.FactoryOS.Application.Platform;
using Divakar.FactoryOS.Application.Reports;
using Divakar.FactoryOS.Application.Inventory;
using Divakar.FactoryOS.Application.QualityControl;
using Divakar.FactoryOS.Application.Maintenance;
using Divakar.FactoryOS.Application.Notifications;
using Divakar.FactoryOS.Application.Auth;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Connectivity;
using Divakar.FactoryOS.Application.Energy;
using Divakar.FactoryOS.Application.Live;
using Divakar.FactoryOS.Application.Production;
using Divakar.FactoryOS.Application.Robotics;
using Divakar.FactoryOS.Application.Settings;
using Divakar.FactoryOS.Application.Plant;
using Divakar.FactoryOS.Application.Tenancy;
using Divakar.FactoryOS.Application.Users;
using Divakar.FactoryOS.Domain.Security;

namespace Divakar.FactoryOS.Api.Endpoints;

public static class Endpoints
{
    public const string AuthRateLimit = "auth";
    public const string RefreshRateLimit = "refresh";
    public const string IngestRateLimit = "ingest";

    public static void MapFactoryOs(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");
        MapAuth(api.MapGroup("/auth").WithTags("Auth"));
        MapPlatform(api.MapGroup("/platform").WithTags("Platform"));
        MapAdmin(api);
        MapPlant(api.MapGroup("/plant").WithTags("Plant"));
        MapConnectivity(api);
        MapEnergy(api);
        MapProduction(api.MapGroup("/production").WithTags("Production"));
        MapRobotics(api.MapGroup("/robotics").WithTags("Robotics"));
        MapAlarms(api.MapGroup("/alarms").WithTags("Alarms"));
        MapMaintenance(api.MapGroup("/maintenance").WithTags("Maintenance"));
        MapQuality(api.MapGroup("/quality").WithTags("Quality"));
        MapInventory(api.MapGroup("/inventory").WithTags("Inventory"));
        MapPlatformFeatures(api);
        api.MapGet("/trace", (string lot, InventoryService s, CancellationToken ct) => s.TraceAsync(lot, ct)).WithTags("Traceability")
            .RequirePermission(Permissions.ProductionRead);
        api.MapGet("/notifications", (NotificationService s, CancellationToken ct) => s.RecentAsync(ct)).WithTags("Notifications")
            .RequirePermission(Permissions.SettingsManage);
    }

    private static void MapAuth(RouteGroupBuilder g)
    {
        g.MapPost("/login", (LoginRequest r, AuthService s, CancellationToken ct) => s.LoginAsync(r, ct))
            .RequireRateLimiting(AuthRateLimit);
        // Every full page load refreshes, so this limit is looser than the password endpoints'.
        g.MapPost("/refresh", (RefreshRequest r, AuthService s, CancellationToken ct) => s.RefreshAsync(r, ct))
            .RequireRateLimiting(RefreshRateLimit);
        g.MapPost("/logout", async (RefreshRequest r, AuthService s, CancellationToken ct) =>
        {
            await s.LogoutAsync(r, ct);
            return Results.NoContent();
        });
        g.MapGet("/me", (ICurrentUser me, AuthService s, CancellationToken ct) => s.MeAsync(me.UserId, ct)).RequireAuthorization();

        g.MapGet("/setup", async (AuthService s, CancellationToken ct) => new { needsSetup = await s.NeedsSetupAsync(ct) });
        // The first platform operator can only be created from the machine the API runs on.
        g.MapPost("/setup", async (SetupRequest r, HttpContext http, AuthService s, IConfiguration config, CancellationToken ct) =>
        {
            // the server itself, plus networks named in Setup:AllowedNetworks (e.g. a Docker network); setup works only once anyway
            var ip = http.Connection.RemoteIpAddress;
            var allowed = (config["Setup:AllowedNetworks"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Any(n => IPNetwork.TryParse(n, out var net) && ip is not null && net.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));
            if (ip is null || (!IPAddress.IsLoopback(ip) && !allowed))
            {
                throw AppException.Forbidden("First-time setup is only allowed on the server itself.");
            }
            return Results.Created("/api/v1/auth/me", new { userId = await s.SetupAsync(r, ct) });
        }).RequireRateLimiting(AuthRateLimit);
    }

    private static void MapPlatform(RouteGroupBuilder g)
    {
        g.MapGet("/plans", (TenantService s, CancellationToken ct) => s.PlansAsync(ct)).RequirePermission(Permissions.PlansManage);
        g.MapPut("/plans/{code}", async (string code, UpdatePlanRequest r, TenantService s, CancellationToken ct) =>
        {
            await s.UpdatePlanAsync(code, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlansManage);

        g.MapGet("/tenants", (TenantService s, CancellationToken ct) => s.TenantsAsync(ct)).RequirePermission(Permissions.TenantsManage);
        g.MapPost("/tenants", async (CreateTenantRequest r, TenantService s, CancellationToken ct) =>
            Results.Created("/api/v1/platform/tenants", new { tenantId = await s.CreateAsync(r, ct) }))
            .RequirePermission(Permissions.TenantsManage);
        g.MapPatch("/tenants/{id:guid}", async (Guid id, UpdateTenantRequest r, TenantService s, CancellationToken ct) =>
        {
            await s.UpdateAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TenantsManage);
    }

    private static void MapAdmin(RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("/", (UserService s, CancellationToken ct) => s.UsersAsync(ct)).RequirePermission(Permissions.UsersRead);
        users.MapPost("/", async (CreateUserRequest r, UserService s, CancellationToken ct) =>
            Results.Created("/api/v1/users", new { userId = await s.CreateAsync(r, ct) })).RequirePermission(Permissions.UsersManage);
        users.MapPatch("/{id:guid}", async (Guid id, UpdateUserRequest r, UserService s, CancellationToken ct) =>
        {
            await s.UpdateAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.UsersManage);

        var roles = api.MapGroup("/roles").WithTags("Roles");
        roles.MapGet("/", (UserService s, CancellationToken ct) => s.RolesAsync(ct)).RequirePermission(Permissions.UsersRead);
        roles.MapGet("/permissions", () => Permissions.TenantAll).RequirePermission(Permissions.RolesManage);
        roles.MapPost("/", async (SaveRoleRequest r, UserService s, CancellationToken ct) =>
            Results.Created("/api/v1/roles", new { roleId = await s.SaveRoleAsync(null, r, ct) })).RequirePermission(Permissions.RolesManage);
        roles.MapPut("/{id:guid}", async (Guid id, SaveRoleRequest r, UserService s, CancellationToken ct) =>
        {
            await s.SaveRoleAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.RolesManage);
        roles.MapDelete("/{id:guid}", async (Guid id, UserService s, CancellationToken ct) =>
        {
            await s.DeleteRoleAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.RolesManage);

        api.MapGet("/audit", (int? take, long? before, AuditQuery q, CancellationToken ct) => q.ReadAsync(take ?? 100, before, ct))
            .WithTags("Audit").RequirePermission(Permissions.AuditRead);
    }

    private static void MapPlant(RouteGroupBuilder g)
    {
        g.MapGet("/catalogue", (PlantService s, CancellationToken ct) => s.CatalogueAsync(ct)).RequirePermission(Permissions.PlantRead);
        g.MapGet("/tree", (PlantService s, CancellationToken ct) => s.TreeAsync(ct)).RequirePermission(Permissions.PlantRead);

        g.MapPost("/nodes", async (SaveNodeRequest r, PlantService s, CancellationToken ct) =>
            Results.Created("/api/v1/plant/tree", new { nodeId = await s.SaveNodeAsync(null, r, ct) })).RequirePermission(Permissions.PlantManage);
        g.MapPut("/nodes/{id:guid}", async (Guid id, SaveNodeRequest r, PlantService s, CancellationToken ct) =>
        {
            await s.SaveNodeAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantManage);
        g.MapDelete("/nodes/{id:guid}", async (Guid id, PlantService s, CancellationToken ct) =>
        {
            await s.DeleteNodeAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantManage);

        g.MapGet("/floor", (FloorService s, CancellationToken ct) => s.FloorAsync(ct)).RequirePermission(Permissions.PlantRead);
        g.MapGet("/assets", (Guid? nodeId, PlantService s, CancellationToken ct) => s.AssetsAsync(nodeId, ct)).RequirePermission(Permissions.PlantRead);
        g.MapGet("/assets/{id:guid}", (Guid id, PlantService s, CancellationToken ct) => s.AssetAsync(id, ct)).RequirePermission(Permissions.PlantRead);
        g.MapPost("/assets", async (SaveAssetRequest r, PlantService s, CancellationToken ct) =>
            Results.Created("/api/v1/plant/assets", new { assetId = await s.SaveAssetAsync(null, r, ct) })).RequirePermission(Permissions.PlantManage);
        g.MapPut("/assets/{id:guid}", async (Guid id, SaveAssetRequest r, PlantService s, CancellationToken ct) =>
        {
            await s.SaveAssetAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantManage);
        g.MapPut("/assets/{id:guid}/layout", async (Guid id, SetLayoutRequest r, PlantService s, CancellationToken ct) =>
        {
            await s.SetLayoutAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantManage);
        g.MapDelete("/assets/{id:guid}", async (Guid id, PlantService s, CancellationToken ct) =>
        {
            await s.DeleteAssetAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantManage);
    }

    private static void MapEnergy(RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings").WithTags("Settings");
        settings.MapGet("/", (SettingsService s, CancellationToken ct) => s.GetAsync(ct)).RequirePermission(Permissions.PlantRead);
        settings.MapPut("/", async (TenantSettings r, SettingsService s, CancellationToken ct) =>
        {
            await s.SaveAsync(r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SettingsManage);

        var g = api.MapGroup("/energy").WithTags("Energy");
        g.MapGet("/summary", (DateTime? from, DateTime? to, EnergyService s, CancellationToken ct) => s.SummaryAsync(from, to, ct))
            .RequirePermission(Permissions.EnergyRead);
        g.MapGet("/power-quality", (EnergyService s, CancellationToken ct) => s.PowerQualityAsync(ct)).RequirePermission(Permissions.EnergyRead);
        g.MapGet("/forecast", (EnergyPlanningService s, CancellationToken ct) => s.ForecastAsync(ct)).RequirePermission(Permissions.EnergyRead);
        g.MapGet("/shifts", (DateOnly? date, EnergyPlanningService s, CancellationToken ct) => s.ShiftsAsync(date, ct)).RequirePermission(Permissions.EnergyRead);
        g.MapGet("/load-shed/rules", (EnergyPlanningService s, CancellationToken ct) => s.RulesAsync(ct)).RequirePermission(Permissions.EnergyRead);
        g.MapPost("/load-shed/rules", async (SaveLoadShedRuleRequest r, EnergyPlanningService s, CancellationToken ct) =>
            Results.Created("/api/v1/energy/load-shed/rules", new { ruleId = await s.SaveRuleAsync(null, r, ct) })).RequirePermission(Permissions.EnergyManage);
        g.MapPut("/load-shed/rules/{id:guid}", async (Guid id, SaveLoadShedRuleRequest r, EnergyPlanningService s, CancellationToken ct) =>
        {
            await s.SaveRuleAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.EnergyManage);
        g.MapDelete("/load-shed/rules/{id:guid}", async (Guid id, EnergyPlanningService s, CancellationToken ct) =>
        {
            await s.DeleteRuleAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.EnergyManage);
        g.MapGet("/load-shed/events", (EnergyPlanningService s, CancellationToken ct) => s.EventsAsync(ct)).RequirePermission(Permissions.EnergyRead);
        g.MapGet("/carbon", (DateTime? from, DateTime? to, EnergyService s, CancellationToken ct) => s.CarbonAsync(from, to, ct))
            .RequirePermission(Permissions.EnergyRead);
    }

    private static void MapProduction(RouteGroupBuilder g)
    {
        g.MapGet("/oee", (DateTime? from, DateTime? to, ProductionService s, CancellationToken ct) => s.OeeAsync(from, to, ct))
            .RequirePermission(Permissions.ProductionRead);
        g.MapGet("/shifts", (DateOnly? date, ProductionService s, CancellationToken ct) => s.ShiftsAsync(date, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapGet("/operator", (OperatorService s, CancellationToken ct) => s.HomeAsync(ct)).RequirePermission(Permissions.ProductionRead);
        g.MapGet("/operator/entries", (Guid? assetId, OperatorService s, CancellationToken ct) => s.EntriesAsync(assetId, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPost("/operator/counts", async (CountRequest r, OperatorService s, CancellationToken ct) =>
            Results.Created("/api/v1/production/operator/entries", new { entryId = await s.CountAsync(r, ct) })).RequirePermission(Permissions.ProductionOperate);
        g.MapGet("/reasons", (ProductionService s, CancellationToken ct) => s.ReasonsAsync(ct)).RequirePermission(Permissions.ProductionRead);
        g.MapGet("/downtimes", (DateTime? from, DateTime? to, Guid? assetId, bool? pending, ProductionService s, CancellationToken ct) =>
            s.DowntimesAsync(from, to, assetId, pending ?? false, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPut("/downtimes/{id:long}/reason", async (long id, ReasonRequest r, ProductionService s, CancellationToken ct) =>
        {
            await s.SetReasonAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionOperate);
        g.MapGet("/pareto", (DateTime? from, DateTime? to, ProductionService s, CancellationToken ct) => s.ParetoAsync(from, to, ct))
            .RequirePermission(Permissions.ProductionRead);

        g.MapGet("/products", (ProductionService s, CancellationToken ct) => s.ProductsAsync(ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPost("/products", async (SaveProductRequest r, ProductionService s, CancellationToken ct) =>
            Results.Created("/api/v1/production/products", new { productId = await s.SaveProductAsync(null, r, ct) })).RequirePermission(Permissions.ProductionManage);
        g.MapPut("/products/{id:guid}", async (Guid id, SaveProductRequest r, ProductionService s, CancellationToken ct) =>
        {
            await s.SaveProductAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionManage);

        g.MapGet("/products/{id:guid}/bom", (Guid id, PlanningService s, CancellationToken ct) => s.BomAsync(id, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPut("/products/{id:guid}/bom", async (Guid id, List<BomLineInput> lines, PlanningService s, CancellationToken ct) =>
        {
            await s.SaveBomAsync(id, lines, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionManage);
        g.MapGet("/products/{id:guid}/routing", (Guid id, PlanningService s, CancellationToken ct) => s.RoutingAsync(id, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPut("/products/{id:guid}/routing", async (Guid id, List<RoutingStepInput> steps, PlanningService s, CancellationToken ct) =>
        {
            await s.SaveRoutingAsync(id, steps, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionManage);
        g.MapGet("/work-orders/{id:guid}/materials", (Guid id, PlanningService s, CancellationToken ct) => s.MaterialsAsync(id, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPost("/work-orders/{id:guid}/issue-materials", (Guid id, PlanningService s, CancellationToken ct) => s.IssueMaterialsAsync(id, ct))
            .RequirePermission(Permissions.ProductionOperate);
        g.MapGet("/work-orders/{id:guid}/steps", (Guid id, PlanningService s, CancellationToken ct) => s.StepsAsync(id, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPost("/work-orders/{id:guid}/steps/{seq:int}/{action:regex(^(start|complete)$)}", async (Guid id, int seq, string action, StepDoneRequest? body,
            PlanningService s, CancellationToken ct) =>
        {
            await s.ChangeStepAsync(id, seq, action, body, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionOperate);
        g.MapGet("/work-orders", (string? status, ProductionService s, CancellationToken ct) => s.WorkOrdersAsync(status, ct))
            .RequirePermission(Permissions.ProductionRead);
        g.MapPost("/work-orders", async (SaveWorkOrderRequest r, ProductionService s, CancellationToken ct) =>
            Results.Created("/api/v1/production/work-orders", new { workOrderId = await s.CreateWorkOrderAsync(r, ct) })).RequirePermission(Permissions.ProductionManage);
        g.MapPost("/work-orders/{id:guid}/{action:regex(^(start|complete|cancel)$)}", async (Guid id, string action, ProductionService s, CancellationToken ct) =>
        {
            await s.ChangeWorkOrderAsync(id, action, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionOperate);
    }

    private static void MapRobotics(RouteGroupBuilder g)
    {
        g.MapGet("/fleet", (RoboticsService s, CancellationToken ct) => s.FleetAsync(ct)).RequirePermission(Permissions.RoboticsRead);
        g.MapGet("/stations", (RoboticsService s, CancellationToken ct) => s.StationsAsync(ct)).RequirePermission(Permissions.RoboticsRead);
        g.MapPost("/stations", async (SaveStationRequest r, RoboticsService s, CancellationToken ct) =>
            Results.Created("/api/v1/robotics/stations", new { stationId = await s.SaveStationAsync(null, r, ct) })).RequirePermission(Permissions.PlantManage);
        g.MapPut("/stations/{id:guid}", async (Guid id, SaveStationRequest r, RoboticsService s, CancellationToken ct) =>
        {
            await s.SaveStationAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantManage);
        g.MapGet("/missions", (bool? active, RoboticsService s, CancellationToken ct) => s.MissionsAsync(active ?? false, ct))
            .RequirePermission(Permissions.RoboticsRead);
        g.MapPost("/missions", async (CreateMissionRequest r, RoboticsService s, CancellationToken ct) =>
            Results.Created("/api/v1/robotics/missions", new { missionId = await s.CreateMissionAsync(r, ct) })).RequirePermission(Permissions.RoboticsOperate);
        g.MapPost("/missions/{id:guid}/cancel", async (Guid id, RoboticsService s, CancellationToken ct) =>
        {
            await s.CancelMissionAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.RoboticsOperate);
    }

    private static void MapAlarms(RouteGroupBuilder g)
    {
        g.MapGet("/", (string? state, DateTime? from, DateTime? to, Guid? assetId, AlarmService s, CancellationToken ct) =>
            s.AlarmsAsync(state, from, to, assetId, ct)).RequirePermission(Permissions.AlarmsRead);
        g.MapGet("/summary", (AlarmService s, CancellationToken ct) => s.SummaryAsync(ct)).RequirePermission(Permissions.AlarmsRead);
        g.MapPost("/{id:long}/ack", async (long id, AckRequest r, AlarmService s, CancellationToken ct) =>
        {
            await s.AcknowledgeAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AlarmsAcknowledge);
        g.MapGet("/rules", (AlarmService s, CancellationToken ct) => s.RulesAsync(ct)).RequirePermission(Permissions.AlarmsRead);
        g.MapPost("/rules", async (SaveRuleRequest r, AlarmService s, CancellationToken ct) =>
            Results.Created("/api/v1/alarms/rules", new { ruleId = await s.SaveRuleAsync(null, r, ct) })).RequirePermission(Permissions.AlarmsManage);
        g.MapPut("/rules/{id:guid}", async (Guid id, SaveRuleRequest r, AlarmService s, CancellationToken ct) =>
        {
            await s.SaveRuleAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AlarmsManage);
        g.MapDelete("/rules/{id:guid}", async (Guid id, AlarmService s, CancellationToken ct) =>
        {
            await s.DeleteRuleAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AlarmsManage);
    }

    private static void MapMaintenance(RouteGroupBuilder g)
    {
        g.MapGet("/kpis", (MaintenanceService s, CancellationToken ct) => s.KpisAsync(ct)).RequirePermission(Permissions.MaintenanceRead);
        g.MapGet("/plans", (MaintenanceService s, CancellationToken ct) => s.PlansAsync(ct)).RequirePermission(Permissions.MaintenanceRead);
        g.MapPost("/plans", async (SavePlanRequest r, MaintenanceService s, CancellationToken ct) =>
            Results.Created("/api/v1/maintenance/plans", new { planId = await s.SavePlanAsync(null, r, ct) })).RequirePermission(Permissions.MaintenanceManage);
        g.MapPut("/plans/{id:guid}", async (Guid id, SavePlanRequest r, MaintenanceService s, CancellationToken ct) =>
        {
            await s.SavePlanAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.MaintenanceManage);
        g.MapGet("/work-orders", (string? status, Guid? assetId, MaintenanceService s, CancellationToken ct) => s.WorkOrdersAsync(status, assetId, ct))
            .RequirePermission(Permissions.MaintenanceRead);
        g.MapPost("/work-orders", async (CreateMaintWoRequest r, MaintenanceService s, CancellationToken ct) =>
            Results.Created("/api/v1/maintenance/work-orders", new { maintWoId = await s.CreateWorkOrderAsync(r, ct) })).RequirePermission(Permissions.MaintenanceExecute);
        g.MapPost("/work-orders/{id:guid}/{action:regex(^(start|complete|cancel)$)}", async (Guid id, string action, MaintenanceService s, CancellationToken ct) =>
        {
            await s.ChangeAsync(id, action, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.MaintenanceExecute);
        g.MapGet("/work-orders/{id:guid}/suggestions", (Guid id, FixKnowledgeService s, CancellationToken ct) => s.ForWorkOrderAsync(id, ct))
            .RequirePermission(Permissions.MaintenanceRead);
        g.MapGet("/alarms/{alarmId:long}/suggestions", (long alarmId, FixKnowledgeService s, CancellationToken ct) => s.ForAlarmAsync(alarmId, ct))
            .RequirePermission(Permissions.MaintenanceRead);
        g.MapGet("/work-orders/{id:guid}/resolution", async (Guid id, FixKnowledgeService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(r) : Results.NoContent()).RequirePermission(Permissions.MaintenanceRead);
        g.MapPut("/work-orders/{id:guid}/resolution", async (Guid id, SaveResolutionRequest r, FixKnowledgeService s, CancellationToken ct) =>
        {
            await s.SaveAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.MaintenanceExecute);
        g.MapGet("/knowledge", (string? q, FixKnowledgeService s, CancellationToken ct) => s.SearchAsync(q, ct)).RequirePermission(Permissions.MaintenanceRead);
        g.MapPost("/knowledge/{id:guid}/helpful", async (Guid id, FixKnowledgeService s, CancellationToken ct) =>
        {
            await s.HelpfulAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.MaintenanceExecute);
        g.MapPut("/work-orders/{id:guid}/checklist", async (Guid id, ChecklistRequest r, MaintenanceService s, CancellationToken ct) =>
        {
            await s.SaveChecklistAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.MaintenanceExecute);
        g.MapPut("/work-orders/{id:guid}/assign", async (Guid id, AssignRequest r, MaintenanceService s, CancellationToken ct) =>
        {
            await s.AssignAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.MaintenanceManage);
    }

    private static void MapQuality(RouteGroupBuilder g)
    {
        g.MapGet("/summary", (QualityService s, CancellationToken ct) => s.SummaryAsync(ct)).RequirePermission(Permissions.QualityRead);
        g.MapGet("/characteristics", (QualityService s, CancellationToken ct) => s.CharacteristicsAsync(ct)).RequirePermission(Permissions.QualityRead);
        g.MapPost("/characteristics", async (SaveCharacteristicRequest r, QualityService s, CancellationToken ct) =>
            Results.Created("/api/v1/quality/characteristics", new { charId = await s.SaveCharacteristicAsync(null, r, ct) })).RequirePermission(Permissions.QualityManage);
        g.MapPut("/characteristics/{id:guid}", async (Guid id, SaveCharacteristicRequest r, QualityService s, CancellationToken ct) =>
        {
            await s.SaveCharacteristicAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.QualityManage);
        g.MapGet("/characteristics/{id:guid}/spc", (Guid id, DateTime? from, DateTime? to, QualityService s, CancellationToken ct) => s.ChartAsync(id, from, to, ct))
            .RequirePermission(Permissions.QualityRead);
        g.MapPost("/measurements", async (MeasurementInput m, QualityService s, CancellationToken ct) =>
        {
            await s.AddMeasurementsAsync(m, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.QualityExecute);
        g.MapGet("/inspection-plans", (InspectionService s, CancellationToken ct) => s.PlansAsync(ct)).RequirePermission(Permissions.QualityRead);
        g.MapPost("/inspection-plans", async (SaveInspectionPlanRequest r, InspectionService s, CancellationToken ct) =>
            Results.Created("/api/v1/quality/inspection-plans", new { planId = await s.SavePlanAsync(null, r, ct) })).RequirePermission(Permissions.QualityManage);
        g.MapPut("/inspection-plans/{id:guid}", async (Guid id, SaveInspectionPlanRequest r, InspectionService s, CancellationToken ct) =>
        {
            await s.SavePlanAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.QualityManage);
        g.MapGet("/inspections/due", (InspectionService s, CancellationToken ct) => s.DueAsync(ct)).RequirePermission(Permissions.QualityRead);
        g.MapGet("/inspections", (InspectionService s, CancellationToken ct) => s.HistoryAsync(ct)).RequirePermission(Permissions.QualityRead);
        g.MapPost("/inspections", (PerformInspectionRequest r, InspectionService s, CancellationToken ct) => s.PerformAsync(r, ct)).RequirePermission(Permissions.QualityExecute);
        g.MapGet("/ncr", (bool? open, QualityService s, CancellationToken ct) => s.NcrsAsync(open ?? false, ct)).RequirePermission(Permissions.QualityRead);
        g.MapPost("/ncr", async (SaveNcrRequest r, QualityService s, CancellationToken ct) =>
            Results.Created("/api/v1/quality/ncr", new { ncId = await s.CreateNcrAsync(r, ct) })).RequirePermission(Permissions.QualityExecute);
        g.MapPut("/ncr/{id:guid}", async (Guid id, SaveNcrRequest r, QualityService s, CancellationToken ct) =>
        {
            await s.UpdateNcrAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.QualityManage);
    }

    private static void MapInventory(RouteGroupBuilder g)
    {
        g.MapGet("/items", (InventoryService s, CancellationToken ct) => s.ItemsAsync(ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPost("/items", async (SaveItemRequest r, InventoryService s, CancellationToken ct) =>
            Results.Created("/api/v1/inventory/items", new { itemId = await s.SaveItemAsync(null, r, ct) })).RequirePermission(Permissions.ProductionManage);
        g.MapPut("/items/{id:guid}", async (Guid id, SaveItemRequest r, InventoryService s, CancellationToken ct) =>
        {
            await s.SaveItemAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionManage);
        g.MapGet("/lots", (Guid? itemId, InventoryService s, CancellationToken ct) => s.LotsAsync(itemId, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapGet("/movements", (Guid? itemId, InventoryService s, CancellationToken ct) => s.MovementsAsync(itemId, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapGet("/lookup", (string code, InventoryService s, CancellationToken ct) => s.LookupAsync(code, ct)).RequirePermission(Permissions.ProductionRead);
        g.MapPost("/receive", async (ReceiveRequest r, InventoryService s, CancellationToken ct) =>
            Results.Created("/api/v1/inventory/lots", new { lotId = await s.ReceiveAsync(r, ct) })).RequirePermission(Permissions.ProductionOperate);
        g.MapPost("/issue", async (IssueRequest r, InventoryService s, CancellationToken ct) =>
        {
            await s.IssueAsync(r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionOperate);
        g.MapPost("/produce", async (ProduceRequest r, InventoryService s, CancellationToken ct) =>
            Results.Created("/api/v1/inventory/lots", new { lotId = await s.ProduceAsync(r, ct) })).RequirePermission(Permissions.ProductionOperate);
        g.MapPost("/adjust", async (AdjustRequest r, InventoryService s, CancellationToken ct) =>
        {
            await s.AdjustAsync(r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ProductionManage);
    }

    private static void MapPlatformFeatures(RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports").WithTags("Reports");
        reports.MapGet("/schedules", (ReportService s, CancellationToken ct) => s.SchedulesAsync(ct)).RequirePermission(Permissions.SettingsManage);
        reports.MapPost("/schedules", async (SaveScheduleRequest r, ReportService s, CancellationToken ct) =>
            Results.Created("/api/v1/reports/schedules", new { scheduleId = await s.SaveScheduleAsync(null, r, ct) })).RequirePermission(Permissions.SettingsManage);
        reports.MapPut("/schedules/{id:guid}", async (Guid id, SaveScheduleRequest r, ReportService s, CancellationToken ct) =>
        {
            await s.SaveScheduleAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SettingsManage);
        reports.MapDelete("/schedules/{id:guid}", async (Guid id, ReportService s, CancellationToken ct) =>
        {
            await s.DeleteScheduleAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SettingsManage);
        reports.MapGet("/{type:regex(^(production|energy|maintenance|quality|alarms)$)}", async (string type, DateTime? from, DateTime? to, string? format,
            ReportService s, CancellationToken ct) =>
        {
            var report = await s.BuildAsync(type, from, to, ct);
            return format == "csv"
                ? Results.File(ReportService.ToCsv(report), "text/csv; charset=utf-8", $"factoryos-{type}-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv")
                : Results.Ok(report);
        }).RequirePermission(Permissions.ReportsRead);

        var dash = api.MapGroup("/dashboards").WithTags("Dashboards");
        dash.MapGet("/", (DashboardService s, CancellationToken ct) => s.ListAsync(ct)).RequirePermission(Permissions.PlantRead);
        dash.MapPost("/", async (SaveDashboardRequest r, DashboardService s, CancellationToken ct) =>
            Results.Created("/api/v1/dashboards", new { dashboardId = await s.SaveAsync(null, r, ct) })).RequirePermission(Permissions.PlantRead);
        dash.MapPut("/{id:guid}", async (Guid id, SaveDashboardRequest r, DashboardService s, CancellationToken ct) =>
        {
            await s.SaveAsync(id, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantRead);
        dash.MapDelete("/{id:guid}", async (Guid id, DashboardService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PlantRead);

        var billing = api.MapGroup("/billing").WithTags("Billing");
        billing.MapGet("/usage", (BillingService s, CancellationToken ct) => s.UsageAsync(ct)).RequirePermission(Permissions.PlantRead);
        billing.MapGet("/invoices", (BillingService s, CancellationToken ct) => s.MyInvoicesAsync(ct)).RequirePermission(Permissions.SettingsManage);
        var platform = api.MapGroup("/platform/billing").WithTags("Platform");
        platform.MapGet("/", (BillingService s, CancellationToken ct) => s.OverviewAsync(ct)).RequirePermission(Permissions.TenantsManage);
        platform.MapPost("/generate", async (DateOnly? month, BillingService s, CancellationToken ct) => new { created = await s.GenerateNowAsync(month, ct) })
            .RequirePermission(Permissions.TenantsManage);
        platform.MapPost("/invoices/{id:guid}/{action:regex(^(paid|void)$)}", async (Guid id, string action, PayRequest r, BillingService s, CancellationToken ct) =>
        {
            await s.SetStatusAsync(id, action == "paid" ? "Paid" : "Void", r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TenantsManage);

        var keys = api.MapGroup("/integrations/api-keys").WithTags("Integrations");
        keys.MapGet("/", (ApiKeyService s, CancellationToken ct) => s.ListAsync(ct)).RequirePermission(Permissions.SettingsManage);
        keys.MapPost("/", async (CreateApiKeyRequest r, ApiKeyService s, CancellationToken ct) => Results.Ok(await s.CreateAsync(r, ct))).RequirePermission(Permissions.SettingsManage);
        keys.MapDelete("/{prefix}", async (string prefix, ApiKeyService s, CancellationToken ct) =>
        {
            await s.RevokeAsync(prefix, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SettingsManage);

        var assistant = api.MapGroup("/assistant").WithTags("Assistant");
        assistant.MapGet("/status", (AssistantService s) => s.Status()).RequirePermission(Permissions.PlantRead);
        assistant.MapPost("/ask", (AskRequest r, AssistantService s, CancellationToken ct) => s.AskAsync(r, ct)).RequirePermission(Permissions.PlantRead);
    }

    private static void MapConnectivity(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/plant").WithTags("Connectivity");
        g.MapGet("/sensor-types", (ConnectivityService s, CancellationToken ct) => s.SensorTypesAsync(ct)).RequirePermission(Permissions.PlantRead);
        g.MapPost("/demo", async (DemoFactoryService s, CancellationToken ct) => Results.Created("/api/v1/plant/tree", await s.CreateAsync(ct)))
            .RequirePermission(Permissions.PlantManage);

        var a = g.MapGroup("/assets/{assetId:guid}");
        a.MapGet("/tags", (Guid assetId, ConnectivityService s, CancellationToken ct) => s.TagsAsync(assetId, ct)).RequirePermission(Permissions.PlantRead);
        a.MapPost("/tags", async (Guid assetId, SaveTagRequest r, ConnectivityService s, CancellationToken ct) =>
            Results.Created($"/api/v1/plant/assets/{assetId}/tags", new { tagId = await s.SaveTagAsync(assetId, null, r, ct) }))
            .RequirePermission(Permissions.ConnectivityManage);
        a.MapPut("/tags/{tagId:guid}", async (Guid assetId, Guid tagId, SaveTagRequest r, ConnectivityService s, CancellationToken ct) =>
        {
            await s.SaveTagAsync(assetId, tagId, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ConnectivityManage);
        a.MapDelete("/tags/{tagId:guid}", async (Guid assetId, Guid tagId, ConnectivityService s, CancellationToken ct) =>
        {
            await s.DeleteTagAsync(assetId, tagId, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ConnectivityManage);
        a.MapPost("/tags/template", async (Guid assetId, ConnectivityService s, CancellationToken ct) =>
            new { added = await s.ApplyTemplateAsync(assetId, ct) }).RequirePermission(Permissions.ConnectivityManage);

        a.MapGet("/connection", (Guid assetId, ConnectivityService s, CancellationToken ct) => s.ConnectionAsync(assetId, ct))
            .RequirePermission(Permissions.ConnectivityManage);
        a.MapPut("/connection", async (Guid assetId, ConnectionSettingsRequest r, ConnectivityService s, CancellationToken ct) =>
        {
            await s.SaveConnectionAsync(assetId, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ConnectivityManage);
        a.MapPost("/test", (Guid assetId, ConnectivityService s, CancellationToken ct) => s.TestAsync(assetId, ct))
            .RequirePermission(Permissions.ConnectivityManage);
        a.MapGet("/live", (Guid assetId, ConnectivityService s, CancellationToken ct) => s.LiveAsync(assetId, ct)).RequirePermission(Permissions.PlantRead);
        a.MapGet("/power-sense", (Guid assetId, PowerSenseService s, CancellationToken ct) => s.GetAsync(assetId, ct)).RequirePermission(Permissions.PlantRead);
        a.MapPut("/power-sense", async (Guid assetId, SavePowerProfileRequest r, PowerSenseService s, CancellationToken ct) =>
        {
            await s.SaveAsync(assetId, r, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.ConnectivityManage);
        a.MapPost("/power-sense/learn", (Guid assetId, PowerSenseService s, CancellationToken ct) => s.LearnAsync(assetId, ct)).RequirePermission(Permissions.ConnectivityManage);
        a.MapGet("/device-keys", (Guid assetId, ConnectivityService s, CancellationToken ct) => s.DeviceKeysAsync(assetId, ct))
            .RequirePermission(Permissions.ConnectivityManage);
        a.MapPost("/device-keys", (Guid assetId, ConnectivityService s, CancellationToken ct) => s.NewDeviceKeyAsync(assetId, ct))
            .RequirePermission(Permissions.ConnectivityManage);

        api.MapGet("/telemetry/history", (Guid tagId, DateTime? from, DateTime? to, ConnectivityService s, CancellationToken ct) =>
            s.HistoryAsync(tagId, from?.ToUniversalTime(), to?.ToUniversalTime(), ct)).WithTags("Telemetry").RequirePermission(Permissions.PlantRead);
        api.MapGet("/connectivity", (ConnectivityService s, CancellationToken ct) => s.ConnectionsAsync(ct))
            .WithTags("Connectivity").RequirePermission(Permissions.PlantRead);

        // Devices post readings with their key in the X-Device-Key header; no user sign-in.
        api.MapPost("/ingest", (HttpRequest http, PushRequest r, ConnectivityService s, CancellationToken ct) =>
            s.PushAsync(http.Headers["X-Device-Key"].FirstOrDefault(), r, ct)).WithTags("Telemetry").RequireRateLimiting(IngestRateLimit);
    }
}
