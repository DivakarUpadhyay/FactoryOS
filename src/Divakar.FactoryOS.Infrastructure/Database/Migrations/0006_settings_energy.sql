-- Phase 4: per-company settings (time zone, tariff, emission factors, contract capacity, shifts, alert e-mails)

CREATE TABLE core.TenantSettings (
    TenantId            uniqueidentifier NOT NULL CONSTRAINT PK_TenantSettings PRIMARY KEY
                                         CONSTRAINT FK_TenantSettings_Tenant REFERENCES core.Tenants(TenantId),
    TimeZone            varchar(64)    NOT NULL CONSTRAINT DF_TenantSettings_TimeZone DEFAULT 'Asia/Taipei',
    Currency            char(3)        NOT NULL CONSTRAINT DF_TenantSettings_Currency DEFAULT 'TWD',
    -- Two-band time-of-use tariff: weekdays between PeakStartHour and PeakEndHour pay PeakRate, all other hours OffPeakRate
    PeakRate            decimal(9,4)   NOT NULL CONSTRAINT DF_TenantSettings_PeakRate DEFAULT 5.20,
    OffPeakRate         decimal(9,4)   NOT NULL CONSTRAINT DF_TenantSettings_OffPeakRate DEFAULT 2.30,
    PeakStartHour       tinyint        NOT NULL CONSTRAINT DF_TenantSettings_PeakStart DEFAULT 16,
    PeakEndHour         tinyint        NOT NULL CONSTRAINT DF_TenantSettings_PeakEnd DEFAULT 22,
    ContractKw          decimal(10,1)  NULL,
    -- kg CO2e per kWh of grid electricity and per Nm3 of natural gas
    ElectricityFactor   decimal(9,4)   NOT NULL CONSTRAINT DF_TenantSettings_ElecFactor DEFAULT 0.474,
    GasFactor           decimal(9,4)   NOT NULL CONSTRAINT DF_TenantSettings_GasFactor DEFAULT 2.09,
    MainMeterAssetId    uniqueidentifier NULL,
    Shifts              nvarchar(max)  NULL CONSTRAINT CK_TenantSettings_Shifts CHECK (Shifts IS NULL OR ISJSON(Shifts) = 1),
    AlertEmails         nvarchar(1000) NULL,
    UpdatedAt           datetime2(0)   NOT NULL CONSTRAINT DF_TenantSettings_UpdatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_TenantSettings_Hours CHECK (PeakStartHour < 24 AND PeakEndHour <= 24 AND PeakStartHour < PeakEndHour)
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.TenantSettings,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.TenantSettings;
GO

-- Time-bucketed reads of one tag type (energy counters, demand) scan Readings by tag and time
CREATE INDEX IX_Tags_Sensor ON plant.Tags(TenantId, SensorTypeCode) INCLUDE (AssetId);
GO
