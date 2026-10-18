-- Fix knowledge base: the cause and the fix recorded when a breakdown is repaired, suggested again when a similar alarm comes back.

CREATE TABLE maintenance.Resolutions (
    MaintWoId       uniqueidentifier NOT NULL CONSTRAINT PK_Resolutions PRIMARY KEY
                                     CONSTRAINT FK_Resolutions_Wo REFERENCES maintenance.WorkOrders(MaintWoId) ON DELETE CASCADE,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Resolutions_Tenant REFERENCES core.Tenants(TenantId),
    FailureCode     nvarchar(40)   NULL,
    Cause           nvarchar(500)  NOT NULL,
    Fix             nvarchar(1000) NOT NULL,
    RecordedBy      uniqueidentifier NULL,
    RecordedAt      datetime2(0)   NOT NULL CONSTRAINT DF_Resolutions_At DEFAULT SYSUTCDATETIME(),
    Helpful         int            NOT NULL CONSTRAINT DF_Resolutions_Helpful DEFAULT 0   -- times a technician reused this fix
);
CREATE INDEX IX_Resolutions_Tenant ON maintenance.Resolutions(TenantId, RecordedAt);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON maintenance.Resolutions,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON maintenance.Resolutions;
