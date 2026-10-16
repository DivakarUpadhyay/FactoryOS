-- Phase 8: quality (SPC characteristics, measurements, non-conformances), inventory (items, lots, movements), traceability

CREATE SCHEMA quality;
GO
CREATE SCHEMA inventory;
GO

-- What is measured: by hand (inspectors enter subgroups) or automatically from a sensor tag
CREATE TABLE quality.Characteristics (
    CharId          uniqueidentifier NOT NULL CONSTRAINT PK_Characteristics PRIMARY KEY CONSTRAINT DF_Characteristics_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Characteristics_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(150)  NOT NULL,
    Unit            nvarchar(20)   NOT NULL CONSTRAINT DF_Characteristics_Unit DEFAULT N'',
    ProductId       uniqueidentifier NULL CONSTRAINT FK_Characteristics_Product REFERENCES production.Products(ProductId),
    AssetId         uniqueidentifier NULL CONSTRAINT FK_Characteristics_Asset REFERENCES plant.Assets(AssetId),
    TagId           uniqueidentifier NULL CONSTRAINT FK_Characteristics_Tag REFERENCES plant.Tags(TagId) ON DELETE SET NULL,
    Nominal         float          NULL,
    Lsl             float          NULL,
    Usl             float          NULL,
    SubgroupSize    tinyint        NOT NULL CONSTRAINT DF_Characteristics_Subgroup DEFAULT 1 CONSTRAINT CK_Characteristics_Subgroup CHECK (SubgroupSize BETWEEN 1 AND 10),
    SampleSeconds   int            NOT NULL CONSTRAINT DF_Characteristics_Sample DEFAULT 60 CONSTRAINT CK_Characteristics_Sample CHECK (SampleSeconds BETWEEN 10 AND 86400),
    IsActive        bit            NOT NULL CONSTRAINT DF_Characteristics_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Characteristics_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Characteristics_Code UNIQUE (TenantId, Code),
    CONSTRAINT CK_Characteristics_Limits CHECK (Lsl IS NULL OR Usl IS NULL OR Lsl < Usl)
);

CREATE TABLE quality.Measurements (
    MeasurementId   bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Measurements PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Measurements_Tenant REFERENCES core.Tenants(TenantId),
    CharId          uniqueidentifier NOT NULL CONSTRAINT FK_Measurements_Char REFERENCES quality.Characteristics(CharId) ON DELETE CASCADE,
    SubgroupNo      bigint         NOT NULL,   -- values entered together share a number
    Value           float          NOT NULL,
    Ts              datetime2(3)   NOT NULL CONSTRAINT DF_Measurements_Ts DEFAULT SYSUTCDATETIME(),
    LotNo           varchar(60)    NULL,
    WorkOrderId     uniqueidentifier NULL CONSTRAINT FK_Measurements_Wo REFERENCES production.WorkOrders(WorkOrderId),
    MeasuredBy      uniqueidentifier NULL,
    Note            nvarchar(200)  NULL
);
CREATE INDEX IX_Measurements_Char ON quality.Measurements(CharId, Ts) INCLUDE (Value, SubgroupNo, LotNo);
CREATE INDEX IX_Measurements_Lot ON quality.Measurements(TenantId, LotNo) WHERE LotNo IS NOT NULL;

CREATE TABLE quality.NonConformances (
    NcId            uniqueidentifier NOT NULL CONSTRAINT PK_Ncr PRIMARY KEY CONSTRAINT DF_Ncr_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Ncr_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    Title           nvarchar(200)  NOT NULL,
    Description     nvarchar(1000) NULL,
    Severity        varchar(10)    NOT NULL CONSTRAINT CK_Ncr_Severity CHECK (Severity IN ('Minor', 'Major', 'Critical')),
    Status          varchar(15)    NOT NULL CONSTRAINT DF_Ncr_Status DEFAULT 'Open'
                                   CONSTRAINT CK_Ncr_Status CHECK (Status IN ('Open', 'Contained', 'Investigating', 'Closed')),
    ProductId       uniqueidentifier NULL CONSTRAINT FK_Ncr_Product REFERENCES production.Products(ProductId),
    AssetId         uniqueidentifier NULL CONSTRAINT FK_Ncr_Asset REFERENCES plant.Assets(AssetId),
    LotNo           varchar(60)    NULL,
    Quantity        int            NULL,
    Disposition     varchar(10)    NULL CONSTRAINT CK_Ncr_Disposition CHECK (Disposition IN ('Rework', 'Scrap', 'UseAsIs', 'Return')),
    RootCause       nvarchar(1000) NULL,
    CorrectiveAction nvarchar(1000) NULL,
    OwnerId         uniqueidentifier NULL CONSTRAINT FK_Ncr_Owner REFERENCES core.Users(UserId),
    DueAt           date           NULL,
    CreatedBy       uniqueidentifier NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Ncr_CreatedAt DEFAULT SYSUTCDATETIME(),
    ClosedAt        datetime2(0)   NULL,
    CONSTRAINT UQ_Ncr_Code UNIQUE (TenantId, Code)
);
CREATE INDEX IX_Ncr_Lot ON quality.NonConformances(TenantId, LotNo) WHERE LotNo IS NOT NULL;

CREATE TABLE inventory.Items (
    ItemId          uniqueidentifier NOT NULL CONSTRAINT PK_Items PRIMARY KEY CONSTRAINT DF_Items_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Items_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(150)  NOT NULL,
    Category        varchar(12)    NOT NULL CONSTRAINT CK_Items_Category CHECK (Category IN ('Raw', 'WIP', 'Finished', 'Spare', 'Consumable')),
    Unit            nvarchar(20)   NOT NULL CONSTRAINT DF_Items_Unit DEFAULT N'pcs',
    MinStock        decimal(14,3)  NOT NULL CONSTRAINT DF_Items_MinStock DEFAULT 0,
    ReorderQty      decimal(14,3)  NULL,
    Location        nvarchar(60)   NULL,
    Barcode         varchar(60)    NULL,
    ProductId       uniqueidentifier NULL CONSTRAINT FK_Items_Product REFERENCES production.Products(ProductId),   -- finished goods made by work orders
    IsActive        bit            NOT NULL CONSTRAINT DF_Items_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Items_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Items_Code UNIQUE (TenantId, Code)
);
CREATE UNIQUE INDEX UX_Items_Barcode ON inventory.Items(TenantId, Barcode) WHERE Barcode IS NOT NULL;

CREATE TABLE inventory.Lots (
    LotId           uniqueidentifier NOT NULL CONSTRAINT PK_Lots PRIMARY KEY CONSTRAINT DF_Lots_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Lots_Tenant REFERENCES core.Tenants(TenantId),
    ItemId          uniqueidentifier NOT NULL CONSTRAINT FK_Lots_Item REFERENCES inventory.Items(ItemId),
    LotNo           varchar(60)    NOT NULL,
    Status          varchar(12)    NOT NULL CONSTRAINT DF_Lots_Status DEFAULT 'Available'
                                   CONSTRAINT CK_Lots_Status CHECK (Status IN ('Available', 'Quarantine', 'Consumed', 'Scrapped')),
    Supplier        nvarchar(100)  NULL,
    SupplierLot     varchar(60)    NULL,
    ExpiresOn       date           NULL,
    WorkOrderId     uniqueidentifier NULL CONSTRAINT FK_Lots_Wo REFERENCES production.WorkOrders(WorkOrderId),   -- produced by
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Lots_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Lots_No UNIQUE (TenantId, LotNo)
);

-- Every stock change; stock on hand = the sum of quantities (receipts and production positive, issues and scrap negative)
CREATE TABLE inventory.Movements (
    MovementId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Movements PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Movements_Tenant REFERENCES core.Tenants(TenantId),
    ItemId          uniqueidentifier NOT NULL CONSTRAINT FK_Movements_Item REFERENCES inventory.Items(ItemId),
    LotId           uniqueidentifier NULL CONSTRAINT FK_Movements_Lot REFERENCES inventory.Lots(LotId),
    Type            varchar(10)    NOT NULL CONSTRAINT CK_Movements_Type CHECK (Type IN ('Receive', 'Issue', 'Produce', 'Adjust', 'Scrap')),
    Quantity        decimal(14,3)  NOT NULL,
    WorkOrderId     uniqueidentifier NULL CONSTRAINT FK_Movements_Wo REFERENCES production.WorkOrders(WorkOrderId),
    MaintWoId       uniqueidentifier NULL CONSTRAINT FK_Movements_MaintWo REFERENCES maintenance.WorkOrders(MaintWoId),
    Reference       nvarchar(100)  NULL,
    UserId          uniqueidentifier NULL,
    Ts              datetime2(0)   NOT NULL CONSTRAINT DF_Movements_Ts DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Movements_Item ON inventory.Movements(TenantId, ItemId, Ts) INCLUDE (Quantity, LotId);
CREATE INDEX IX_Movements_Lot ON inventory.Movements(LotId) INCLUDE (Quantity, Type, WorkOrderId) WHERE LotId IS NOT NULL;
CREATE INDEX IX_Movements_Wo ON inventory.Movements(WorkOrderId) INCLUDE (LotId, Quantity, Type) WHERE WorkOrderId IS NOT NULL;

-- Quality alarms come from SPC on sensor-fed characteristics
ALTER TABLE alarms.Alarms DROP CONSTRAINT CK_Alarms_Kind;
ALTER TABLE alarms.Alarms ADD CONSTRAINT CK_Alarms_Kind CHECK (Kind IN ('Rule', 'Anomaly', 'Quality'));
CREATE UNIQUE INDEX UX_Alarms_OpenQuality ON alarms.Alarms(TagId) WHERE ClearedAt IS NULL AND Kind = 'Quality';
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.Characteristics,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.Characteristics,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.Measurements,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.Measurements,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.NonConformances,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.NonConformances,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON inventory.Items,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON inventory.Items,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON inventory.Lots,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON inventory.Lots,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON inventory.Movements,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON inventory.Movements;
GO
