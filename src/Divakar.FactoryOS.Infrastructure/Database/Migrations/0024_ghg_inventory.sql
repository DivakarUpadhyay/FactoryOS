-- A fuller GHG inventory (GHG Protocol Corporate Standard / ISO 14064-1): fugitive refrigerant losses and process emissions
-- in Scope 1, Scope 3 activities by category, grid factors for more countries, and the data a product carbon footprint needs.

ALTER TABLE enms.EmissionFactors DROP CONSTRAINT CK_EmissionFactors_Method;
ALTER TABLE enms.EmissionFactors ADD CONSTRAINT CK_EmissionFactors_Method
    CHECK (Method IN ('Combustion', 'Location', 'Market', 'Fugitive', 'Process', 'Activity'));
ALTER TABLE enms.EmissionFactors ADD Scope3Category tinyint NULL
    CONSTRAINT CK_EmissionFactors_S3Cat CHECK (Scope3Category IS NULL OR Scope3Category BETWEEN 1 AND 15);
GO

-- Activity data for factors that are not energy: kg of refrigerant topped up (mass balance), tonnes of limestone calcined,
-- tonne-km shipped, passenger-km flown, tonnes of waste sent to landfill…
CREATE TABLE enms.GhgActivities (
    ActivityId      uniqueidentifier NOT NULL CONSTRAINT PK_GhgActivities PRIMARY KEY CONSTRAINT DF_GhgActivities_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_GhgActivities_Tenant REFERENCES core.Tenants(TenantId),
    FactorId        uniqueidentifier NOT NULL CONSTRAINT FK_GhgActivities_Factor REFERENCES enms.EmissionFactors(FactorId),
    PeriodFrom      date           NOT NULL,
    PeriodTo        date           NOT NULL,
    Quantity        decimal(16,4)  NOT NULL CONSTRAINT CK_GhgActivities_Qty CHECK (Quantity > 0),
    Reference       nvarchar(200)  NULL,          -- invoice, waste transfer note, service log
    Note            nvarchar(500)  NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_GhgActivities_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_GhgActivities_Period CHECK (PeriodFrom <= PeriodTo)
);
CREATE INDEX IX_GhgActivities_Period ON enms.GhgActivities(TenantId, PeriodFrom);

-- Product carbon footprint inputs: the cradle-to-gate factor of each purchased material, and the mass of a finished unit
ALTER TABLE inventory.Items ADD KgCo2ePerUnit decimal(14,6) NULL, Co2eSource nvarchar(200) NULL;
ALTER TABLE production.Products ADD MassKg decimal(12,4) NULL;
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON enms.GhgActivities,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON enms.GhgActivities;
GO
