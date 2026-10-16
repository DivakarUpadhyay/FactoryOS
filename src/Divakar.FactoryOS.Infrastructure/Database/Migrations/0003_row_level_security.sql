-- Divakar FactoryOS 0003: row-level security
-- The API sets SESSION_CONTEXT('TenantId') and SESSION_CONTEXT('IsSuperAdmin') on every connection it opens.
-- A tenant user can then only see and write its own rows, even if a query forgets a WHERE clause.
-- Platform operators (IsSuperAdmin = 1) see every tenant. Requests with no context (login lookups) are
-- handled by the 'system' flag, which only the API's own login code sets.

CREATE SCHEMA sec;
GO

CREATE FUNCTION sec.fn_TenantAccess(@TenantId uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS Allowed
    WHERE CAST(SESSION_CONTEXT(N'IsSuperAdmin') AS bit) = 1
       OR CAST(SESSION_CONTEXT(N'System') AS bit) = 1
       OR @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
GO

CREATE SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.Users,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.Users,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.AuditLog,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.Roles,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.Roles,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Nodes,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Nodes,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Assets,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Assets
WITH (STATE = ON);
GO
