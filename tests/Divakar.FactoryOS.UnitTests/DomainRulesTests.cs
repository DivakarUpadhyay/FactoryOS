using Divakar.FactoryOS.Application.Common;
using Divakar.FactoryOS.Domain.Plant;
using Divakar.FactoryOS.Domain.Security;
using Divakar.FactoryOS.Domain.Tenancy;

namespace Divakar.FactoryOS.UnitTests;

public sealed class DomainRulesTests
{
    [Theory]
    [InlineData(null, "Site", true)]
    [InlineData(null, "Plant", false)]
    [InlineData("Site", "Plant", true)]
    [InlineData("Plant", "Area", true)]
    [InlineData("Area", "Line", true)]
    [InlineData("Line", "Cell", true)]
    [InlineData("Site", "Line", false)]
    [InlineData("Cell", "Cell", false)]
    [InlineData("Site", "Bogus", false)]
    public void Hierarchy_levels_follow_isa95(string? parent, string child, bool valid) =>
        Assert.Equal(valid, NodeLevels.IsValidChild(parent, child));

    [Fact]
    public void Trial_tenants_may_sign_in_only_until_the_trial_ends()
    {
        var now = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(TenantStatuses.CanSignIn("Trial", now.AddDays(1), now));
        Assert.False(TenantStatuses.CanSignIn("Trial", now.AddDays(-1), now));
        Assert.True(TenantStatuses.CanSignIn("Active", null, now));
        Assert.False(TenantStatuses.CanSignIn("Suspended", null, now));
    }

    [Fact]
    public void Built_in_roles_use_only_tenant_permissions_and_admin_has_all()
    {
        foreach (var role in SystemRoles.Templates)
        {
            Assert.All(role.Permissions, p => Assert.Contains(p, Permissions.TenantAll));
        }
        var admin = SystemRoles.Templates.Single(r => r.Code == SystemRoles.FactoryAdmin);
        Assert.Equal(Permissions.TenantAll.Count, admin.Permissions.Count);
        Assert.DoesNotContain(Permissions.TenantAll, Permissions.PlatformOnly.Contains);
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("onlyletterslong")]
    [InlineData("123456789012")]
    public void Weak_passwords_are_rejected(string password) =>
        Assert.Throws<AppException>(() => Guard.Password(password));

    [Fact]
    public void Codes_are_lower_case_and_safe()
    {
        Assert.Equal("line-01", Guard.Code(" Line-01 ", "Code"));
        Assert.Throws<AppException>(() => Guard.Code("line 01", "Code"));
        Assert.Throws<AppException>(() => Guard.Code("-x", "Code"));
    }
}
