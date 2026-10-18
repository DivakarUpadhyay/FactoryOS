-- Power sense: machine state and cycle count derived from one power (or current) signal, for machines without a PLC

CREATE TABLE plant.PowerProfiles (
    AssetId         uniqueidentifier NOT NULL CONSTRAINT PK_PowerProfiles PRIMARY KEY CONSTRAINT FK_PowerProfiles_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_PowerProfiles_Tenant REFERENCES core.Tenants(TenantId),
    SourceTagId     uniqueidentifier NOT NULL,              -- active_power or current tag of the asset
    StateTagId      uniqueidentifier NOT NULL,              -- virtual machine_state tag written by power sense
    CountTagId      uniqueidentifier NULL,                  -- virtual part_count tag (one cycle per power burst)
    OffBelow        float          NOT NULL,                -- below: off / stopped
    RunAbove        float          NOT NULL,                -- above: running; between: idle
    CountCycles     bit            NOT NULL CONSTRAINT DF_PowerProfiles_Count DEFAULT 1,
    MinCycleSec     int            NOT NULL CONSTRAINT DF_PowerProfiles_MinCycle DEFAULT 5,
    AutoLearn       bit            NOT NULL CONSTRAINT DF_PowerProfiles_Auto DEFAULT 1,
    LearnedAt       datetime2(0)   NULL,
    LearnedFrom     int            NULL,                    -- readings used for the last learning
    LastTs          datetime2(3)   NULL,                    -- newest source reading already processed
    LastAbove       bit            NOT NULL CONSTRAINT DF_PowerProfiles_LastAbove DEFAULT 0,
    LastCycleAt     datetime2(3)   NULL,
    CycleCount      float          NOT NULL CONSTRAINT DF_PowerProfiles_Cycles DEFAULT 0,
    CONSTRAINT CK_PowerProfiles_Bands CHECK (OffBelow >= 0 AND RunAbove > OffBelow)
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.PowerProfiles,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.PowerProfiles;
GO
