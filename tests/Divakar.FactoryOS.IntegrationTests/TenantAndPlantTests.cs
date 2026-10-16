using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Divakar.FactoryOS.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class TenantAndPlantTests(FactoryOsApp app)
{
    private static async Task<Guid> CreateNode(HttpClient c, Guid? parentId, string level, string code)
    {
        var res = await c.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId, level, code, name = code.ToUpperInvariant() });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<IdResponse>())!.NodeId;
    }

    private static async Task<Guid> CreateLine(HttpClient c, string prefix)
    {
        var site = await CreateNode(c, null, "Site", prefix + "-site");
        var plant = await CreateNode(c, site, "Plant", prefix + "-plant");
        var area = await CreateNode(c, plant, "Area", prefix + "-area");
        return await CreateNode(c, area, "Line", prefix + "-line");
    }

    [Fact]
    public async Task New_tenant_gets_the_eight_built_in_roles_and_an_admin_with_every_permission()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/v1/roles");
        Assert.Equal(8, roles.GetArrayLength());
        var me = await admin.GetFromJsonAsync<MeDto>("/api/v1/auth/me");
        Assert.False(me!.IsSuperAdmin);
        Assert.Contains("plant.manage", me.Permissions);
        Assert.DoesNotContain("platform.tenants.manage", me.Permissions);
        Assert.Contains("robotics", me.Modules);
    }

    [Fact]
    public async Task Tenant_admin_cannot_reach_platform_endpoints()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task Hierarchy_builds_and_assets_appear_in_the_tree()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var line = await CreateLine(admin, "a");
        var res = await admin.PostAsJsonAsync("/api/v1/plant/assets", new
        {
            nodeId = line, assetTypeCode = "robot-arm", protocolCode = "opcua", code = "robot-01", name = "Welding robot 1",
            vendor = "FANUC", ratedPowerKw = 7.5, idealCycleSec = 42.0, installedOn = "2025-03-01",
        });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var tree = await admin.GetFromJsonAsync<JsonElement>("/api/v1/plant/tree");
        var lineNode = tree[0].GetProperty("children")[0].GetProperty("children")[0].GetProperty("children")[0];
        Assert.Equal("Line", lineNode.GetProperty("node").GetProperty("level").GetString());
        Assert.Equal("robot-01", lineNode.GetProperty("assets")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Wrong_hierarchy_levels_are_rejected()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var site = await CreateNode(admin, null, "Site", "s1");
        var lineUnderSite = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = site, level = "Line", code = "l1", name = "L1" });
        Assert.Equal(HttpStatusCode.BadRequest, lineUnderSite.StatusCode);
        var plantAtTop = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { level = "Plant", code = "p1", name = "P1" });
        Assert.Equal(HttpStatusCode.BadRequest, plantAtTop.StatusCode);
    }

    [Fact]
    public async Task Tenants_never_see_each_others_data()
    {
        var (_, _, a) = await app.NewTenantAsync();
        var (_, _, b) = await app.NewTenantAsync();
        var line = await CreateLine(a, "iso");
        var created = await a.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = line, assetTypeCode = "cnc", code = "cnc-1", name = "CNC 1" });
        var assetId = (await created.Content.ReadFromJsonAsync<IdResponse>())!.AssetId;

        Assert.Equal(0, (await b.GetFromJsonAsync<JsonElement>("/api/v1/plant/tree")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/plant/assets/{assetId}")).StatusCode);
        // B also cannot hang its own asset under A's line
        var cross = await b.PostAsJsonAsync("/api/v1/plant/assets", new { nodeId = line, assetTypeCode = "cnc", code = "x", name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
    }

    [Fact]
    public async Task Row_level_security_hides_other_tenants_even_without_a_where_clause()
    {
        var (tenantA, _, a) = await app.NewTenantAsync();
        var (tenantB, _, _) = await app.NewTenantAsync();
        await CreateLine(a, "rls");

        await using var con = new SqlConnection(FactoryOsApp.ConnectionString);
        await con.OpenAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = """
            EXEC sp_set_session_context N'TenantId', @t;
            SELECT COUNT(*) FROM plant.Nodes WHERE TenantId = @a;
            """;
        cmd.Parameters.AddWithValue("@t", tenantB);
        cmd.Parameters.AddWithValue("@a", tenantA);
        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Operator_role_can_read_the_plant_but_not_change_it()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var email = $"op{Guid.NewGuid():N}"[..12] + "@x.test";
        var res = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            email, displayName = "Line operator", password = FactoryOsApp.Password, roles = new[] { "operator" },
        });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var op = await app.SignInAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/plant/tree")).StatusCode);
        var write = await op.PostAsJsonAsync("/api/v1/plant/nodes", new { level = "Site", code = "nope", name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Plan_limits_are_enforced()
    {
        var (_, _, admin) = await app.NewTenantAsync(plan: "trial");   // trial: 1 plant
        var site = await CreateNode(admin, null, "Site", "lim-site");
        await CreateNode(admin, site, "Plant", "lim-plant-1");
        var second = await admin.PostAsJsonAsync("/api/v1/plant/nodes", new { parentId = site, level = "Plant", code = "lim-plant-2", name = "P2" });
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
    }

    [Fact]
    public async Task The_last_factory_admin_cannot_be_deactivated()
    {
        var (_, adminEmail, admin) = await app.NewTenantAsync();
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var adminId = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == adminEmail).GetProperty("userId").GetGuid();
        var res = await admin.PatchAsJsonAsync($"/api/v1/users/{adminId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Custom_roles_cannot_take_platform_permissions()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var res = await admin.PostAsJsonAsync("/api/v1/roles", new { code = "sneaky", name = "Sneaky", permissions = new[] { "platform.tenants.manage" } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var ok = await admin.PostAsJsonAsync("/api/v1/roles", new { code = "line-lead", name = "Line lead", permissions = new[] { "plant.read", "production.operate" } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Audit_log_records_changes_and_shows_tenants_only_their_own()
    {
        var (tenantA, _, a) = await app.NewTenantAsync();
        await CreateNode(a, null, "Site", "audit-site");
        var entries = await a.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        var list = entries.EnumerateArray().ToList();
        Assert.Contains(list, e => e.GetProperty("action").GetString() == "node.create");
        Assert.Contains(list, e => e.GetProperty("action").GetString() == "login");
        Assert.All(list, e => Assert.Equal(tenantA, e.GetProperty("tenantId").GetGuid()));
    }

    [Fact]
    public async Task Catalogue_lists_every_protocol_family_and_asset_type()
    {
        var (_, _, admin) = await app.NewTenantAsync();
        var cat = await admin.GetFromJsonAsync<JsonElement>("/api/v1/plant/catalogue");
        var protocols = cat.GetProperty("protocols").EnumerateArray().Select(p => p.GetProperty("protocolCode").GetString()).ToList();
        foreach (var p in new[] { "opcua", "mqtt", "sparkplugb", "modbus-tcp", "s7", "ethernet-ip", "secs-gem", "bacnet", "dlms", "iec61850", "vda5050", "ros2", "lorawan" })
        {
            Assert.Contains(p, protocols);
        }
        Assert.True(cat.GetProperty("assetTypes").GetArrayLength() >= 30);
    }
}
