-- Counts entered by operators on the shop-floor app (machines that count nothing, or scrap found by hand).
-- Each entry also moves a virtual counter tag (virtual:manual-good / virtual:manual-scrap), so OEE and reports pick it up.

CREATE TABLE production.OperatorEntries (
    EntryId         bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OperatorEntries PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_OperatorEntries_Tenant REFERENCES core.Tenants(TenantId),
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_OperatorEntries_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    Good            int            NOT NULL CONSTRAINT CK_OperatorEntries_Good CHECK (Good >= 0),
    Scrap           int            NOT NULL CONSTRAINT CK_OperatorEntries_Scrap CHECK (Scrap >= 0),
    ScrapReason     nvarchar(80)   NULL,
    Note            nvarchar(300)  NULL,
    EnteredBy       uniqueidentifier NULL,
    EnteredAt       datetime2(3)   NOT NULL CONSTRAINT DF_OperatorEntries_At DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_OperatorEntries_Asset ON production.OperatorEntries(TenantId, AssetId, EnteredAt);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.OperatorEntries,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.OperatorEntries;
