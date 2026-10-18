-- IP cameras: a snapshot URL per asset (most IP cameras serve one JPEG per request), shown on the asset page
-- and captured automatically when an alarm is raised on the asset.

CREATE TABLE plant.Cameras (
    AssetId         uniqueidentifier NOT NULL CONSTRAINT PK_Cameras PRIMARY KEY CONSTRAINT FK_Cameras_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Cameras_Tenant REFERENCES core.Tenants(TenantId),
    SnapshotUrl     nvarchar(500)  NOT NULL,
    Username        nvarchar(100)  NULL,
    Password        nvarchar(400)  NULL,   -- encrypted (enc:v1:)
    SnapOnAlarm     bit            NOT NULL CONSTRAINT DF_Cameras_SnapOnAlarm DEFAULT 1,
    UpdatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Cameras_UpdatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE alarms.Snapshots (
    AlarmId         bigint         NOT NULL CONSTRAINT PK_AlarmSnapshots PRIMARY KEY CONSTRAINT FK_AlarmSnapshots_Alarm REFERENCES alarms.Alarms(AlarmId) ON DELETE CASCADE,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_AlarmSnapshots_Tenant REFERENCES core.Tenants(TenantId),
    Image           varbinary(max) NULL,
    ContentType     varchar(40)    NULL,
    Error           nvarchar(200)  NULL,   -- why the camera could not be read (kept so it is not retried forever)
    CapturedAt      datetime2(0)   NOT NULL CONSTRAINT DF_AlarmSnapshots_At DEFAULT SYSUTCDATETIME()
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Cameras,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.Cameras,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON alarms.Snapshots,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON alarms.Snapshots;
