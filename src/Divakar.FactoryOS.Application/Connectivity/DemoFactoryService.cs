using Divakar.FactoryOS.Application.Abstractions;
using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Application.Plant;
using Divakar.FactoryOS.Application.Tenancy;

namespace Divakar.FactoryOS.Application.Connectivity;

public sealed record DemoResult(int Nodes, int Assets, int Tags, int SkippedForPlanLimit);

/// <summary>
/// Builds a complete demo plant on the built-in simulator: production lines, robots and AGVs, utilities,
/// energy and power metering, safety and environment sensors, every asset with its standard tags.
/// </summary>
public sealed class DemoFactoryService(
    IPlantRepository plant, IConnectivityRepository conn, ITenantRepository tenants, Robotics.IRoboticsRepository robotics,
    Alarms.IAlarmRepository alarms, Maintenance.IMaintenanceRepository maintenance, Production.IProductionRepository production,
    QualityControl.IQualityRepository quality, Inventory.IInventoryRepository inventory, Production.IPlanningRepository planning, QualityControl.IInspectionRepository inspections, ICurrentUser me, IAuditLog audit)
{
    /// <summary>Typical alarm rules, so a new demo raises real alarms as the simulator drifts and breaks down.</summary>
    private static readonly Alarms.SaveRuleRequest[] Rules =
    [
        new("Machine fault", "machine_state", null, null, "=", 4, 0, "Critical", CreateWorkOrder: true),
        new("Spindle vibration high", "vibration_rms", null, "cnc", ">", 4.5, 30, "Critical", CreateWorkOrder: true),
        new("Robot joint overheating", "robot_joint_temp", null, null, ">", 75, 60, "Warning"),
        new("AGV battery low", "battery_soc", null, "agv", "<", 20, 30, "Warning"),
        new("Power factor low", "power_factor", null, "energy-meter", "<", 0.85, 300, "Warning"),
        new("Compressed-air pressure low", "pressure", null, "air-compressor", "<", 5.5, 60, "Warning"),
    ];

    /// <summary>Preventive plans on the demo's key machines (code of the asset, plan).</summary>
    private static readonly (string Asset, Maintenance.SavePlanRequest Plan)[] Plans =
    [
        ("demo-cnc-01", new(Guid.Empty, "Monthly CNC service", "Calendar", 30,
            ["Check way-lube level", "Clean chip conveyor", "Inspect spindle taper", "Check coolant concentration"], 90)),
        ("demo-ac-01", new(Guid.Empty, "Compressor 2,000 h service", "RunHours", 2000,
            ["Replace air filter", "Change oil and oil filter", "Check belt tension", "Drain condensate"], 120)),
        ("demo-rb-01", new(Guid.Empty, "Robot grease every 50,000 cycles", "Cycles", 50000, ["Grease J1–J3 reducers", "Check cable dress pack"], 60)),
    ];

    /// <summary>Pick-up and drop-off points on the simulated AGV loop (a 160 m × 60 m rectangle from (20, 20) to (180, 80)).</summary>
    private static readonly Robotics.SaveStationRequest[] Stations =
    [
        new("wh-in", "Goods-in warehouse", 40, 20),
        new("smt-feed", "SMT line feeder", 120, 20),
        new("mach-feed", "Machining cell buffer", 180, 50),
        new("mold-out", "Moulding output", 110, 80),
        new("fg-store", "Finished-goods store", 20, 50),
    ];

    private sealed record A(string Code, string Name, string Type, decimal? Kw, string? Vendor = null, decimal? CycleSec = null);
    private sealed record Group(string Level, string Code, string Name, A[] Assets, Group[]? Children = null);

    private static readonly Group Site = new("Site", "demo-site", "Taoyuan Smart Campus", [], [
        new("Plant", "demo-plant-a", "Plant A – Electronics & Machining", [
            new A("demo-tr-main", "Main transformer 22.8 kV / 380 V", "transformer", 2000m, "Fortune Electric"),
            new A("demo-em-main", "Main incomer energy meter", "energy-meter", null, "Schneider"),
            new A("demo-pq-main", "Main power quality analyser", "power-analyzer", null, "Janitza"),
            new A("demo-ups-01", "Data-room UPS", "ups", 40m, "Delta"),
        ], [
            new("Area", "demo-smt", "SMT Hall", [new A("demo-em-smt", "SMT hall sub-meter", "energy-meter", null, "Schneider")], [
                new("Line", "demo-smt-l1", "SMT Line 1", [
                    new A("demo-smt-l1-pp", "Pick-and-place 1", "smt", 15m, "Fuji", 28m),
                    new A("demo-smt-l1-rf", "Reflow oven 1", "reflow", 55m, "Heller"),
                    new A("demo-smt-l1-aoi", "AOI camera 1", "camera", 2m, "Koh Young"),
                    new A("demo-smt-l1-cv", "Line 1 conveyor", "conveyor", 3m),
                ]),
                new("Line", "demo-smt-l2", "SMT Line 2", [
                    new A("demo-smt-l2-pp", "Pick-and-place 2", "smt", 15m, "Fuji", 30m),
                    new A("demo-smt-l2-rf", "Reflow oven 2", "reflow", 55m, "Heller"),
                    new A("demo-smt-l2-pk", "Packaging 2", "packaging", 6m),
                ]),
            ]),
            new("Area", "demo-mach", "Machining Hall", [new A("demo-em-mach", "Machining hall sub-meter", "energy-meter", null, "Schneider")], [
                new("Line", "demo-cnc-l1", "CNC Cell Line", [
                    new A("demo-cnc-01", "5-axis CNC 1", "cnc", 22m, "Hurco", 95m),
                    new A("demo-cnc-02", "5-axis CNC 2", "cnc", 22m, "Hurco", 95m),
                    new A("demo-cnc-03", "Turning centre 3", "cnc", 18m, "Victor Taichung", 60m),
                    new A("demo-cnc-04", "Turning centre 4", "cnc", 18m, "Victor Taichung", 60m),
                    new A("demo-rb-01", "Machine-tending robot", "robot-arm", 7.5m, "FANUC", 45m),
                    new A("demo-cb-01", "Deburring cobot", "cobot", 1.5m, "Techman Robot", 35m),
                ]),
            ]),
            new("Area", "demo-mould", "Moulding Hall", [], [
                new("Line", "demo-mould-l1", "Injection Line", [
                    new A("demo-inj-01", "Injection moulder 180 t", "injection", 45m, "Chen Hsong", 32m),
                    new A("demo-inj-02", "Injection moulder 250 t", "injection", 60m, "Chen Hsong", 40m),
                    new A("demo-inj-03", "Injection moulder 350 t", "injection", 75m, "Fu Chun Shin", 48m),
                    new A("demo-rb-02", "Sprue picker robot", "robot-arm", 3m, "Yushin", 30m),
                    new A("demo-press-01", "Stamping press 200 t", "press", 37m, "Chin Fong", 4m),
                ]),
            ]),
            new("Area", "demo-log", "Logistics", [
                new A("demo-agv-01", "AGV tugger 1", "agv", 3m, "Hiwin"),
                new A("demo-agv-02", "AGV tugger 2", "agv", 3m, "Hiwin"),
                new A("demo-amr-01", "AMR 1", "amr", 1.2m, "MiR"),
                new A("demo-rfid-01", "Dock RFID gate", "rfid-reader", 0.2m, "Zebra"),
                new A("demo-evc-01", "Forklift charger", "ev-charger", 22m, "Delta"),
            ]),
            new("Area", "demo-util", "Utilities", [
                new A("demo-ac-01", "Air compressor 1", "air-compressor", 75m, "Atlas Copco"),
                new A("demo-ac-02", "Air compressor 2", "air-compressor", 55m, "Atlas Copco"),
                new A("demo-ch-01", "Chiller 1", "chiller", 180m, "Trane"),
                new A("demo-bl-01", "Steam boiler", "boiler", 15m),
                new A("demo-hvac-01", "AHU SMT hall", "hvac", 30m, "Daikin"),
                new A("demo-hvac-02", "AHU machining hall", "hvac", 30m, "Daikin"),
                new A("demo-wm-01", "Main water meter", "water-meter", null),
                new A("demo-gm-01", "Main gas meter", "gas-meter", null),
                new A("demo-pv-01", "Rooftop solar 300 kWp", "solar-inverter", 300m, "Delta"),
                new A("demo-bess-01", "Battery storage 500 kWh", "battery-storage", 250m),
            ]),
            new("Area", "demo-ehs", "Safety & Environment", [
                new A("demo-gas-01", "Gas detector boiler room", "gas-detector", null),
                new A("demo-sp-01", "Press safety controller", "safety-plc", null, "Pilz"),
                new A("demo-env-01", "Environment sensor SMT hall", "sensor", null),
                new A("demo-env-02", "Environment sensor machining hall", "sensor", null),
                new A("demo-th-01", "Thermal camera switchgear", "thermal-camera", null, "FLIR"),
            ]),
        ]),
    ]);

    /// <summary>Products, SPC characteristics (two fed by cycle-time sensors, one measured by hand) and stock with lots.</summary>
    private async Task SeedQualityAndStockAsync(Guid tenantId, IReadOnlyDictionary<string, Guid> assets, CancellationToken ct)
    {
        if ((await production.ProductsAsync(tenantId, ct)).Count > 0)
        {
            return;
        }
        var bracket = await production.SaveProductAsync(tenantId, null, new Production.SaveProductRequest("brkt-01", "Steel bracket", 90, "pcs"), ct);
        var cap = await production.SaveProductAsync(tenantId, null, new Production.SaveProductRequest("cap-01", "Moulded cap", 36, "pcs"), ct);

        async Task<Guid?> Tag(string assetCode, string tagCode) => assets.TryGetValue(assetCode, out var a)
            ? (await conn.TagsAsync(tenantId, a, ct)).FirstOrDefault(t => t.Code == tagCode)?.TagId : null;
        if (await Tag("demo-inj-01", "cycle") is { } injCycle)
        {
            await quality.SaveCharacteristicAsync(tenantId, null, new QualityControl.SaveCharacteristicRequest("inj1-cycle", "Moulding cycle time", "s", cap,
                assets["demo-inj-01"], injCycle, 37.5, 34, 41, 1, 60), ct);
        }
        if (await Tag("demo-cnc-01", "cycle") is { } cncCycle)
        {
            await quality.SaveCharacteristicAsync(tenantId, null, new QualityControl.SaveCharacteristicRequest("cnc1-cycle", "CNC cycle time", "s", bracket,
                assets["demo-cnc-01"], cncCycle, 90, 82, 98, 1, 60), ct);
        }
        var hole = await quality.SaveCharacteristicAsync(tenantId, null, new QualityControl.SaveCharacteristicRequest("brkt-hole", "Bracket hole diameter", "mm",
            bracket, assets.GetValueOrDefault("demo-cnc-01") is var c && c != Guid.Empty ? c : null, null, 10.0, 9.95, 10.05, 5, 3600), ct);
        var rng = new Random(11);
        for (var i = 0; i < 25; i++)
        {
            // five parts per subgroup around 10.00 mm, standard deviation about 0.012 mm
            var values = Enumerable.Range(0, 5).Select(_ => Math.Round(10.0 + 0.012 * Normal(rng), 4)).ToList();
            await quality.AddMeasurementsAsync(tenantId, hole, values, $"FG-DEMO-{i / 5 + 1:00}", null, null, me.UserId, ct);
            await Task.Delay(2, ct);   // distinct subgroup numbers
        }

        (string Code, string Name, string Category, string Unit, decimal Min, decimal Receive, Guid? Product, string? Supplier)[] items =
        [
            ("abs-resin", "ABS resin pellets", "Raw", "kg", 200, 500, null, "Chi Mei"),
            ("steel-bar-40", "Steel bar Ø40 mm", "Raw", "m", 50, 120, null, "China Steel"),
            ("solder-sac305", "Solder paste SAC305", "Consumable", "jar", 5, 12, null, "Shenmao"),
            ("brg-6205", "Bearing 6205-2RS", "Spare", "pcs", 4, 3, null, "NSK"),
            ("filter-ac", "Compressor air filter", "Spare", "pcs", 2, 6, null, "Atlas Copco"),
            ("fg-brkt", "Steel bracket (finished)", "Finished", "pcs", 0, 0, bracket, null),
            ("fg-cap", "Moulded cap (finished)", "Finished", "pcs", 0, 0, cap, null),
        ];
        var itemIds = new Dictionary<string, Guid>();
        foreach (var it in items)
        {
            var id = itemIds[it.Code] = await inventory.SaveItemAsync(tenantId, null, new Inventory.SaveItemRequest(it.Code, it.Name, it.Category, it.Unit, it.Min, it.Min * 2,
                it.Category == "Spare" ? "Maintenance store" : it.Category == "Finished" ? "FG warehouse" : "Raw store A", it.Code.ToUpperInvariant(), it.Product), ct);
            if (it.Receive > 0)
            {
                var lot = await inventory.CreateLotAsync(tenantId, id, $"DEMO-{it.Code.ToUpperInvariant()}-01", it.Supplier, null, null, null, ct);
                await inventory.AddMovementAsync(tenantId, id, lot, "Receive", it.Receive, null, null, "Opening stock", me.UserId, ct);
            }
        }

        // bills of materials and routings for the two demo products
        await planning.SaveBomAsync(tenantId, bracket, [new(itemIds["steel-bar-40"], 0.25m, 2m)], ct);
        await planning.SaveBomAsync(tenantId, cap, [new(itemIds["abs-resin"], 0.018m, 3m)], ct);
        await planning.SaveRoutingAsync(tenantId, bracket,
        [
            new("Saw bar to length", null, 20, 10),
            new("CNC machining", assets.TryGetValue("demo-cnc-01", out var c1) ? c1 : null, 90, 30),
            new("Deburr and inspect", null, 25, 0),
        ], ct);
        await planning.SaveRoutingAsync(tenantId, cap, [new("Injection moulding", assets.TryGetValue("demo-inj-01", out var i1) ? i1 : null, 36, 45), new("Pack", null, 5, 0)], ct);

        // inspection plans: first piece and a patrol check every 50 brackets
        await inspections.SavePlanAsync(tenantId, null, new QualityControl.SaveInspectionPlanRequest(bracket, "First-piece check", "FirstPiece", null, true, true,
            [new("Hole diameter 10.00 ± 0.05 mm", hole), new("No burrs on edges", null), new("Correct drawing revision", null)]), ct);
        await inspections.SavePlanAsync(tenantId, null, new QualityControl.SaveInspectionPlanRequest(bracket, "Patrol check", "EveryN", 50, true, true,
            [new("Hole diameter 10.00 ± 0.05 mm", hole), new("Surface free of scratches", null)]), ct);
        await inspections.SavePlanAsync(tenantId, null, new QualityControl.SaveInspectionPlanRequest(cap, "Final inspection", "Final", null, true, true,
            [new("Colour matches master sample", null), new("No short shots or flash", null)]), ct);
    }

    private static double Normal(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    public async Task<DemoResult> CreateAsync(CancellationToken ct)
    {
        var tenantId = me.RequireTenant();
        if (await plant.NodeCodeExistsAsync(tenantId, Site.Code, null, ct))
        {
            throw AppException.Conflict("The demo factory already exists. Delete its site first to build it again.");
        }
        var tenant = await tenants.TenantAsync(tenantId, ct) ?? throw AppException.NotFound("Tenant");
        var limits = await tenants.PlanAsync(tenant.PlanCode, ct) ?? throw AppException.NotFound("Plan");
        var usage = await tenants.UsageAsync(tenantId, ct);
        var room = limits.MaxAssets - usage.Assets;
        if (usage.Plants >= limits.MaxPlants)
        {
            throw AppException.Limit($"Your plan allows {limits.MaxPlants} plants and they are all used.");
        }
        var sensors = (await conn.SensorTypesAsync(ct)).ToDictionary(s => s.SensorTypeCode);
        int nodes = 0, assets = 0, tags = 0, skipped = 0;

        async Task Build(Group g, Guid? parent)
        {
            var nodeId = await plant.SaveNodeAsync(tenantId, null,
                new SaveNodeRequest(parent, g.Level, g.Code, g.Name, "Demo data from the built-in simulator", null, null, null, null), ct);
            nodes++;
            foreach (var a in g.Assets)
            {
                if (assets >= room)
                {
                    skipped++;
                    continue;
                }
                var assetId = await plant.SaveAssetAsync(tenantId, null, new SaveAssetRequest(nodeId, a.Type, "simulator", a.Code, a.Name,
                    a.Vendor, null, null, null, a.Kw, a.CycleSec, null, null, null), ct);
                assets++;
                foreach (var t in await conn.TemplatesAsync(a.Type, ct))
                {
                    var s = sensors[t.SensorTypeCode];
                    await conn.SaveTagAsync(tenantId, assetId, null, new SaveTagRequest(t.TagCode, t.Name, s.SensorTypeCode, s.Unit, null, 1, 0,
                        ConnectivityService.DefaultDeadband(s), s.MinValue, s.MaxValue, true), s.Unit, ct);
                    tags++;
                }
            }
            foreach (var child in g.Children ?? [])
            {
                await Build(child, nodeId);
            }
        }

        await Build(Site, null);
        var existing = (await robotics.StationsAsync(tenantId, ct)).Select(x => x.Code).ToHashSet();
        foreach (var st in Stations.Where(x => !existing.Contains(x.Code)))
        {
            await robotics.SaveStationAsync(tenantId, null, st, ct);
        }
        if ((await alarms.RulesAsync(tenantId, ct)).Count == 0)
        {
            foreach (var rule in Rules)
            {
                await alarms.SaveRuleAsync(tenantId, null, rule, ct);
            }
        }
        var assetsByCode = (await plant.AssetsAsync(tenantId, null, ct)).ToDictionary(a => a.Code, a => a.AssetId);
        foreach (var (code, p) in Plans.Where(x => assetsByCode.ContainsKey(x.Asset)))
        {
            await maintenance.SavePlanAsync(tenantId, null, p with { AssetId = assetsByCode[code] }, System.Text.Json.JsonSerializer.Serialize(p.Checklist), ct);
        }
        await SeedQualityAndStockAsync(tenantId, assetsByCode, ct);
        await audit.WriteAsync("demo.create", "node", Site.Code, new { nodes, assets, tags, skipped }, ct);
        return new DemoResult(nodes, assets, tags, skipped);
    }
}
