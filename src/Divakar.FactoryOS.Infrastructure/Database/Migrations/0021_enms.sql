-- ISO 50001:2018 energy management system (EnMS): emission factor library, energy data that no meter reads (bills, fuel
-- purchases, green power certificates), significant energy uses, EnPIs with their energy baselines (ISO 50006), objectives
-- and action plans, documented information (policy, scope, legal register, energy team…) and audit / nonconformity /
-- management review records.

CREATE SCHEMA enms;
GO

-- One row per factor version. Fuels keep the published per-TJ factors and the net heat value per unit, so kg CO2e per unit is
-- computed (CO2 + CH4 x GWP + N2O x GWP) and can be checked against the source; electricity keeps the published kg CO2e/kWh.
CREATE TABLE enms.EmissionFactors (
    FactorId        uniqueidentifier NOT NULL CONSTRAINT PK_EmissionFactors PRIMARY KEY CONSTRAINT DF_EmissionFactors_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_EmissionFactors_Tenant REFERENCES core.Tenants(TenantId),
    Source          varchar(30)    NOT NULL,      -- electricity, natural_gas, diesel, gasoline, lpg, fuel_oil, steam
    Name            nvarchar(150)  NOT NULL,
    Scope           tinyint        NOT NULL CONSTRAINT CK_EmissionFactors_Scope CHECK (Scope IN (1, 2, 3)),
    Method          varchar(12)    NOT NULL CONSTRAINT CK_EmissionFactors_Method CHECK (Method IN ('Combustion', 'Location', 'Market')),
    Region          varchar(10)    NOT NULL,
    Year            smallint       NOT NULL,
    Unit            varchar(10)    NOT NULL,      -- kWh, Nm3, L, kg
    HeatKcalPerUnit decimal(10,1)  NULL,
    Co2KgPerTj      decimal(12,3)  NULL,
    Ch4KgPerTj      decimal(12,3)  NULL,
    N2oKgPerTj      decimal(12,3)  NULL,
    KgCo2ePerUnit   decimal(12,6)  NULL,          -- published total (electricity, supplier or market factors)
    GwpSet          varchar(10)    NOT NULL CONSTRAINT DF_EmissionFactors_Gwp DEFAULT 'AR5',
    Reference       nvarchar(400)  NOT NULL,
    IsActive        bit            NOT NULL CONSTRAINT DF_EmissionFactors_Active DEFAULT 1,
    UpdatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_EmissionFactors_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_EmissionFactors_Value CHECK (KgCo2ePerUnit IS NOT NULL OR (HeatKcalPerUnit IS NOT NULL AND Co2KgPerTj IS NOT NULL))
);
CREATE UNIQUE INDEX UX_EmissionFactors ON enms.EmissionFactors(TenantId, Source, Method, Region, Year);

-- Energy that no connected meter reads: monthly bills, fuel deliveries (forklift diesel, generator, boiler LPG), and green
-- power bought with certificates (T-REC) or a power purchase agreement, which lowers market-based Scope 2.
CREATE TABLE enms.EnergyEntries (
    EntryId         uniqueidentifier NOT NULL CONSTRAINT PK_EnergyEntries PRIMARY KEY CONSTRAINT DF_EnergyEntries_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_EnergyEntries_Tenant REFERENCES core.Tenants(TenantId),
    Source          varchar(30)    NOT NULL,      -- a factor source, or green_power
    PeriodFrom      date           NOT NULL,
    PeriodTo        date           NOT NULL,
    Quantity        decimal(14,3)  NOT NULL CONSTRAINT CK_EnergyEntries_Qty CHECK (Quantity >= 0),
    Unit            varchar(10)    NOT NULL,
    Cost            decimal(14,2)  NULL,
    Instrument      varchar(20)    NULL,          -- green power: T-REC, PPA, Self
    Reference       nvarchar(200)  NULL,          -- bill or certificate number
    Note            nvarchar(500)  NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_EnergyEntries_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_EnergyEntries_Period CHECK (PeriodFrom <= PeriodTo)
);
CREATE INDEX IX_EnergyEntries_Period ON enms.EnergyEntries(TenantId, PeriodFrom);

-- Significant energy uses (ISO 50001 6.3): assets that account for substantial consumption or improvement potential.
CREATE TABLE enms.Seus (
    SeuId             uniqueidentifier NOT NULL CONSTRAINT PK_Seus PRIMARY KEY CONSTRAINT DF_Seus_Id DEFAULT NEWSEQUENTIALID(),
    TenantId          uniqueidentifier NOT NULL CONSTRAINT FK_Seus_Tenant REFERENCES core.Tenants(TenantId),
    Name              nvarchar(150)  NOT NULL,
    AssetIds          nvarchar(max)  NOT NULL CONSTRAINT CK_Seus_Assets CHECK (ISJSON(AssetIds) = 1),
    RelevantVariables nvarchar(300)  NULL,
    OperatingCriteria nvarchar(1000) NULL,
    Owner             nvarchar(150)  NULL,
    CreatedAt         datetime2(0)   NOT NULL CONSTRAINT DF_Seus_Created DEFAULT SYSUTCDATETIME()
);

-- Energy performance indicators with their energy baseline (ISO 50001 6.4, 6.5; ISO 50006). Boundary is the whole site
-- (main meters) or one SEU. Regression EnPIs keep the fitted model E = a + b x (x = parts made) and its quality.
CREATE TABLE enms.Enpis (
    EnpiId          uniqueidentifier NOT NULL CONSTRAINT PK_Enpis PRIMARY KEY CONSTRAINT DF_Enpis_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Enpis_Tenant REFERENCES core.Tenants(TenantId),
    Name            nvarchar(150)  NOT NULL,
    SeuId           uniqueidentifier NULL CONSTRAINT FK_Enpis_Seu REFERENCES enms.Seus(SeuId),
    Kind            varchar(12)    NOT NULL CONSTRAINT CK_Enpis_Kind CHECK (Kind IN ('Measured', 'Ratio', 'Regression')),
    BaselineFrom    date           NOT NULL,
    BaselineTo      date           NOT NULL,
    BaselineKwh     float          NULL,
    BaselineParts   float          NULL,
    BaselineDays    int            NULL,
    Intercept       float          NULL,
    Slope           float          NULL,
    R2              float          NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Enpis_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Enpis_Period CHECK (BaselineFrom < BaselineTo)
);

-- Objectives and energy targets (6.2) and the action plans that reach them, with verified savings (9.1, ISO 50015).
CREATE TABLE enms.Objectives (
    ObjectiveId     uniqueidentifier NOT NULL CONSTRAINT PK_Objectives PRIMARY KEY CONSTRAINT DF_Objectives_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Objectives_Tenant REFERENCES core.Tenants(TenantId),
    Title           nvarchar(200)  NOT NULL,
    EnpiId          uniqueidentifier NULL CONSTRAINT FK_Objectives_Enpi REFERENCES enms.Enpis(EnpiId),
    TargetPct       decimal(5,2)   NOT NULL,      -- improvement of the EnPI against its baseline
    TargetDate      date           NOT NULL,
    Owner           nvarchar(150)  NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Objectives_Created DEFAULT SYSUTCDATETIME()
);

CREATE TABLE enms.Actions (
    ActionId        uniqueidentifier NOT NULL CONSTRAINT PK_Actions PRIMARY KEY CONSTRAINT DF_Actions_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Actions_Tenant REFERENCES core.Tenants(TenantId),
    ObjectiveId     uniqueidentifier NULL CONSTRAINT FK_Actions_Objective REFERENCES enms.Objectives(ObjectiveId) ON DELETE SET NULL,
    Title           nvarchar(200)  NOT NULL,
    Owner           nvarchar(150)  NULL,
    DueDate         date           NULL,
    ExpectedKwhYear float          NULL,
    Investment      decimal(14,2)  NULL,
    Verification    nvarchar(300)  NULL,          -- how the improvement is verified
    VerifiedKwhYear float          NULL,
    Status          varchar(12)    NOT NULL CONSTRAINT CK_Actions_Status CHECK (Status IN ('Planned', 'InProgress', 'Done', 'Verified')),
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Actions_Created DEFAULT SYSUTCDATETIME()
);

-- Documented information the standard asks for, one kind per clause: energy policy (5.2), scope and boundaries (4.3),
-- interested parties (4.2), legal and other requirements (6.1 / 9.1.2), energy team (5.3), risks and opportunities (6.1),
-- data collection plan (6.6), competence (7.2), communication (7.4), design (8.2), procurement (8.3).
CREATE TABLE enms.Documents (
    DocumentId      uniqueidentifier NOT NULL CONSTRAINT PK_Documents PRIMARY KEY CONSTRAINT DF_Documents_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Documents_Tenant REFERENCES core.Tenants(TenantId),
    Kind            varchar(20)    NOT NULL,
    Title           nvarchar(200)  NOT NULL,
    Body            nvarchar(max)  NULL,
    Owner           nvarchar(150)  NULL,
    ApprovedBy      nvarchar(150)  NULL,
    ApprovedOn      date           NULL,
    ReviewDue       date           NULL,
    UpdatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Documents_Updated DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Documents_Kind ON enms.Documents(TenantId, Kind);

-- Internal audits (9.2), management reviews (9.3) and nonconformities with corrective action (10.1).
CREATE TABLE enms.Findings (
    FindingId       uniqueidentifier NOT NULL CONSTRAINT PK_Findings PRIMARY KEY CONSTRAINT DF_Findings_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Findings_Tenant REFERENCES core.Tenants(TenantId),
    Kind            varchar(20)    NOT NULL CONSTRAINT CK_Findings_Kind CHECK (Kind IN ('Audit', 'ManagementReview', 'Nonconformity')),
    Clause          varchar(10)    NULL,
    Title           nvarchar(200)  NOT NULL,
    Detail          nvarchar(2000) NULL,
    RootCause       nvarchar(1000) NULL,
    CorrectiveAction nvarchar(1000) NULL,
    Owner           nvarchar(150)  NULL,
    HeldOn          date           NOT NULL,
    DueDate         date           NULL,
    ClosedOn        date           NULL
);
CREATE INDEX IX_Findings_Kind ON enms.Findings(TenantId, Kind, HeldOn);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.EmissionFactors,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.EmissionFactors,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.EnergyEntries,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.EnergyEntries,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Seus,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Seus,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Enpis,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Enpis,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Objectives,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Objectives,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Actions,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Actions,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Documents,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Documents,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Findings,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Findings;
GO
