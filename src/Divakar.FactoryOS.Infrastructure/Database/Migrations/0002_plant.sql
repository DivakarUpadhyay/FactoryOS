-- Divakar FactoryOS 0002: ISA-95 plant hierarchy, asset catalogue, protocols and assets

-- Reference data shared by all tenants
CREATE TABLE plant.AssetTypes (
    AssetTypeCode   varchar(40)    NOT NULL CONSTRAINT PK_AssetTypes PRIMARY KEY,
    Category        varchar(30)    NOT NULL CONSTRAINT CK_AssetTypes_Category
                                   CHECK (Category IN ('Production','Robotics','Energy','Utility','Control','Sensing','Vision','Logistics','Safety')),
    Name            nvarchar(100)  NOT NULL,
    Icon            varchar(40)    NOT NULL
);

CREATE TABLE plant.Protocols (
    ProtocolCode    varchar(40)    NOT NULL CONSTRAINT PK_Protocols PRIMARY KEY,
    Family          varchar(30)    NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    Transport       varchar(30)    NOT NULL,
    DefaultPort     int            NULL,
    Notes           nvarchar(300)  NULL
);

-- One self-referencing table for the whole hierarchy: Site > Plant > Area > Line > Cell
CREATE TABLE plant.Nodes (
    NodeId          uniqueidentifier NOT NULL CONSTRAINT PK_Nodes PRIMARY KEY CONSTRAINT DF_Nodes_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Nodes_Tenant REFERENCES core.Tenants(TenantId),
    ParentId        uniqueidentifier NULL CONSTRAINT FK_Nodes_Parent REFERENCES plant.Nodes(NodeId),
    Level           varchar(10)    NOT NULL CONSTRAINT CK_Nodes_Level CHECK (Level IN ('Site','Plant','Area','Line','Cell')),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(150)  NOT NULL,
    Description     nvarchar(500)  NULL,
    Latitude        decimal(9,6)   NULL,
    Longitude       decimal(9,6)   NULL,
    LayoutX         int            NULL,   -- position on the parent's floor plan
    LayoutY         int            NULL,
    IsActive        bit            NOT NULL CONSTRAINT DF_Nodes_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Nodes_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Nodes_Code UNIQUE (TenantId, Code)
);
CREATE INDEX IX_Nodes_Parent ON plant.Nodes(TenantId, ParentId);

CREATE TABLE plant.Assets (
    AssetId         uniqueidentifier NOT NULL CONSTRAINT PK_Assets PRIMARY KEY CONSTRAINT DF_Assets_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Assets_Tenant REFERENCES core.Tenants(TenantId),
    NodeId          uniqueidentifier NOT NULL CONSTRAINT FK_Assets_Node REFERENCES plant.Nodes(NodeId),
    AssetTypeCode   varchar(40)    NOT NULL CONSTRAINT FK_Assets_Type REFERENCES plant.AssetTypes(AssetTypeCode),
    ProtocolCode    varchar(40)    NULL CONSTRAINT FK_Assets_Protocol REFERENCES plant.Protocols(ProtocolCode),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(150)  NOT NULL,
    Vendor          nvarchar(100)  NULL,
    Model           nvarchar(100)  NULL,
    SerialNo        nvarchar(100)  NULL,
    Endpoint        nvarchar(300)  NULL,   -- e.g. opc.tcp://10.0.0.5:4840, mqtt topic root, modbus host:port/unit
    RatedPowerKw    decimal(10,2)  NULL,
    IdealCycleSec   decimal(10,3)  NULL,   -- for OEE performance
    Status          varchar(20)    NOT NULL CONSTRAINT DF_Assets_Status DEFAULT 'Offline'
                                   CONSTRAINT CK_Assets_Status CHECK (Status IN ('Offline','Idle','Running','Stopped','Fault','Maintenance')),
    InstalledOn     date           NULL,
    LayoutX         int            NULL,
    LayoutY         int            NULL,
    IsActive        bit            NOT NULL CONSTRAINT DF_Assets_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Assets_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Assets_Code UNIQUE (TenantId, Code)
);
CREATE INDEX IX_Assets_Node ON plant.Assets(TenantId, NodeId);
