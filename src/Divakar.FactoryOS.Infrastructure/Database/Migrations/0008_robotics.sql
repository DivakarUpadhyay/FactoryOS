-- Phase 6: robotics and AGV fleet — map stations and transport missions (dispatched as VDA 5050 orders to real fleets)

CREATE SCHEMA robotics;
GO

-- Named points on the fleet map (metres), where vehicles pick up and drop off
CREATE TABLE robotics.Stations (
    StationId       uniqueidentifier NOT NULL CONSTRAINT PK_Stations PRIMARY KEY CONSTRAINT DF_Stations_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Stations_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    X               decimal(9,2)   NOT NULL,
    Y               decimal(9,2)   NOT NULL,
    CONSTRAINT UQ_Stations_Code UNIQUE (TenantId, Code)
);

CREATE TABLE robotics.Missions (
    MissionId       uniqueidentifier NOT NULL CONSTRAINT PK_Missions PRIMARY KEY CONSTRAINT DF_Missions_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Missions_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    FromStationId   uniqueidentifier NOT NULL CONSTRAINT FK_Missions_From REFERENCES robotics.Stations(StationId),
    ToStationId     uniqueidentifier NOT NULL CONSTRAINT FK_Missions_To REFERENCES robotics.Stations(StationId),
    Priority        tinyint        NOT NULL CONSTRAINT DF_Missions_Priority DEFAULT 2 CONSTRAINT CK_Missions_Priority CHECK (Priority BETWEEN 1 AND 3),
    Payload         nvarchar(100)  NULL,
    VehicleAssetId  uniqueidentifier NULL CONSTRAINT FK_Missions_Vehicle REFERENCES plant.Assets(AssetId),
    Status          varchar(20)    NOT NULL CONSTRAINT DF_Missions_Status DEFAULT 'Queued'
                                   CONSTRAINT CK_Missions_Status CHECK (Status IN ('Queued','Assigned','PickedUp','Done','Failed','Cancelled')),
    OrderId         varchar(64)    NULL,   -- VDA 5050 orderId sent to the vehicle
    Error           nvarchar(300)  NULL,
    CreatedBy       uniqueidentifier NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Missions_CreatedAt DEFAULT SYSUTCDATETIME(),
    AssignedAt      datetime2(0)   NULL,
    PickedUpAt      datetime2(0)   NULL,
    FinishedAt      datetime2(0)   NULL,
    CONSTRAINT UQ_Missions_Code UNIQUE (TenantId, Code),
    CONSTRAINT CK_Missions_Stations CHECK (FromStationId <> ToStationId)
);
CREATE INDEX IX_Missions_Status ON robotics.Missions(TenantId, Status, Priority, CreatedAt);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON robotics.Stations,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON robotics.Stations,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON robotics.Missions,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON robotics.Missions;
GO

-- Vehicle state is read with the MQTT driver (JSON paths into the VDA 5050 state topic); orders are published by the fleet dispatcher
UPDATE plant.Protocols SET SupportLevel = 'Native',
       Notes = N'Reads the vehicle state topic (JSON paths) and sends transport orders (VDA 5050 v2 order messages) over MQTT'
WHERE ProtocolCode = 'vda5050';
GO
