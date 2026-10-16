-- Electricity bill forecast (basic charge per contract kW) and automatic load shedding

ALTER TABLE core.TenantSettings ADD DemandChargePerKw decimal(9,2) NULL;   -- monthly basic charge per kW of contract capacity
GO

CREATE SCHEMA energy;
GO

-- When demand passes ShedAboveKw, loads are shed one by one in priority order (1 first); they return in reverse order
-- once demand stays below RestoreBelowKw. A load is shed by notification only, or by writing a value to one of its tags.
CREATE TABLE energy.LoadShedRules (
    RuleId          uniqueidentifier NOT NULL CONSTRAINT PK_LoadShedRules PRIMARY KEY CONSTRAINT DF_LoadShedRules_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_LoadShedRules_Tenant REFERENCES core.Tenants(TenantId),
    Name            nvarchar(150)  NOT NULL,
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_LoadShedRules_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    Priority        tinyint        NOT NULL CONSTRAINT CK_LoadShedRules_Priority CHECK (Priority BETWEEN 1 AND 99),
    ShedAboveKw     decimal(10,1)  NOT NULL,
    RestoreBelowKw  decimal(10,1)  NOT NULL,
    Action          varchar(10)    NOT NULL CONSTRAINT CK_LoadShedRules_Action CHECK (Action IN ('Notify', 'Write')),
    WriteTagId      uniqueidentifier NULL,
    ShedValue       float          NULL,
    RestoreValue    float          NULL,
    IsActive        bit            NOT NULL CONSTRAINT DF_LoadShedRules_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_LoadShedRules_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_LoadShedRules_Band CHECK (RestoreBelowKw < ShedAboveKw),
    CONSTRAINT CK_LoadShedRules_Write CHECK (Action = 'Notify' OR (WriteTagId IS NOT NULL AND ShedValue IS NOT NULL AND RestoreValue IS NOT NULL))
);

CREATE TABLE energy.ShedEvents (
    EventId         bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShedEvents PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_ShedEvents_Tenant REFERENCES core.Tenants(TenantId),
    RuleId          uniqueidentifier NOT NULL CONSTRAINT FK_ShedEvents_Rule REFERENCES energy.LoadShedRules(RuleId) ON DELETE CASCADE,
    DemandKw        float          NOT NULL,
    ShedAt          datetime2(0)   NOT NULL CONSTRAINT DF_ShedEvents_ShedAt DEFAULT SYSUTCDATETIME(),
    RestoredAt      datetime2(0)   NULL,
    Result          nvarchar(300)  NULL
);
CREATE UNIQUE INDEX UX_ShedEvents_Open ON energy.ShedEvents(RuleId) WHERE RestoredAt IS NULL;
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON energy.LoadShedRules,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON energy.LoadShedRules,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON energy.ShedEvents,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON energy.ShedEvents;
GO
