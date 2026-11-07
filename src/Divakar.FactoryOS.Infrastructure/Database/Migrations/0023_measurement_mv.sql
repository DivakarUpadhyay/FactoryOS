-- ISO 50001 9.1 asks that measuring equipment used for EnPIs and the energy baseline is accurate and repeatable, with records
-- of calibration or other means of establishing accuracy. ISO 50015 / IPMVP / ASHRAE Guideline 14 describe how savings are
-- measured and verified, with their uncertainty.

-- The meter register: every meter (or virtual meter) the data collection plan relies on, its accuracy class and calibration cycle.
CREATE TABLE enms.Meters (
    MeterId          uniqueidentifier NOT NULL CONSTRAINT PK_Meters PRIMARY KEY CONSTRAINT DF_Meters_Id DEFAULT NEWSEQUENTIALID(),
    TenantId         uniqueidentifier NOT NULL CONSTRAINT FK_Meters_Tenant REFERENCES core.Tenants(TenantId),
    Name             nvarchar(150)  NOT NULL,
    AssetId          uniqueidentifier NULL CONSTRAINT FK_Meters_Asset REFERENCES plant.Assets(AssetId),
    Measures         varchar(20)    NOT NULL CONSTRAINT CK_Meters_Measures
                                    CHECK (Measures IN ('electricity', 'gas', 'water', 'air', 'steam', 'fuel', 'temperature', 'production', 'other')),
    Kind             varchar(10)    NOT NULL CONSTRAINT CK_Meters_Kind CHECK (Kind IN ('Main', 'Sub', 'Portable', 'Virtual')),
    SerialNo         nvarchar(60)   NULL,
    Make             nvarchar(100)  NULL,
    AccuracyClass    nvarchar(30)   NULL,          -- e.g. IEC 62053-22 class 0.5S, MID class B
    UncertaintyPct   decimal(6,3)   NULL,          -- expanded measurement uncertainty, % of reading
    IntervalMonths   int            NULL CONSTRAINT CK_Meters_Interval CHECK (IntervalMonths IS NULL OR IntervalMonths BETWEEN 1 AND 240),
    LastCalibratedOn date           NULL,
    Location         nvarchar(150)  NULL,
    IsActive         bit            NOT NULL CONSTRAINT DF_Meters_Active DEFAULT 1,
    CreatedAt        datetime2(0)   NOT NULL CONSTRAINT DF_Meters_Created DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Meters_Asset ON enms.Meters(TenantId, AssetId);

-- Calibration and verification records, newest is the meter's current state.
CREATE TABLE enms.Calibrations (
    CalibrationId    uniqueidentifier NOT NULL CONSTRAINT PK_Calibrations PRIMARY KEY CONSTRAINT DF_Calibrations_Id DEFAULT NEWSEQUENTIALID(),
    TenantId         uniqueidentifier NOT NULL CONSTRAINT FK_Calibrations_Tenant REFERENCES core.Tenants(TenantId),
    MeterId          uniqueidentifier NOT NULL CONSTRAINT FK_Calibrations_Meter REFERENCES enms.Meters(MeterId) ON DELETE CASCADE,
    CalibratedOn     date           NOT NULL,
    Result           varchar(10)    NOT NULL CONSTRAINT CK_Calibrations_Result CHECK (Result IN ('Pass', 'Adjusted', 'Fail')),
    AsFoundErrorPct  decimal(8,3)   NULL,
    AsLeftErrorPct   decimal(8,3)   NULL,
    Lab              nvarchar(150)  NULL,          -- accredited laboratory (ISO/IEC 17025) or in-house check
    CertificateNo    nvarchar(80)   NULL,
    Note             nvarchar(500)  NULL,
    CreatedAt        datetime2(0)   NOT NULL CONSTRAINT DF_Calibrations_Created DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Calibrations_Meter ON enms.Calibrations(MeterId, CalibratedOn DESC);

-- Measurement and verification plans (ISO 50015, IPMVP). Options B (retrofit isolation, SEU boundary) and C (whole facility)
-- are evaluated from the metered data against an EnPI's baseline model; A and D keep the savings the engineer documents.
CREATE TABLE enms.MvPlans (
    PlanId              uniqueidentifier NOT NULL CONSTRAINT PK_MvPlans PRIMARY KEY CONSTRAINT DF_MvPlans_Id DEFAULT NEWSEQUENTIALID(),
    TenantId            uniqueidentifier NOT NULL CONSTRAINT FK_MvPlans_Tenant REFERENCES core.Tenants(TenantId),
    Name                nvarchar(150)  NOT NULL,
    ActionId            uniqueidentifier NULL CONSTRAINT FK_MvPlans_Action REFERENCES enms.Actions(ActionId) ON DELETE SET NULL,
    EnpiId              uniqueidentifier NULL CONSTRAINT FK_MvPlans_Enpi REFERENCES enms.Enpis(EnpiId),
    MvOption            char(1)        NOT NULL CONSTRAINT CK_MvPlans_Option CHECK (MvOption IN ('A', 'B', 'C', 'D')),
    Boundary            nvarchar(300)  NULL,
    ReportingFrom       date           NOT NULL,
    ReportingTo         date           NOT NULL,
    NonRoutineKwh       float          NOT NULL CONSTRAINT DF_MvPlans_Nra DEFAULT 0,   -- non-routine adjustment to the baseline
    StaticFactors       nvarchar(500)  NULL,
    ConfidencePct       tinyint        NOT NULL CONSTRAINT DF_MvPlans_Conf DEFAULT 90 CONSTRAINT CK_MvPlans_Conf CHECK (ConfidencePct IN (68, 80, 90, 95)),
    ManualSavingsKwh    float          NULL,       -- options A and D
    ManualUncertaintyPct float         NULL,
    CreatedAt           datetime2(0)   NOT NULL CONSTRAINT DF_MvPlans_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_MvPlans_Period CHECK (ReportingFrom < ReportingTo)
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Meters,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Meters,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Calibrations,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.Calibrations,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.MvPlans,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.MvPlans;
GO
