-- Phase 5: production — products, work orders, automatically detected downtime with operator reasons

-- When the server stored each latest value (database clock), so live screens never miss values whose device time is late or early
ALTER TABLE telemetry.Latest ADD UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_Latest_UpdatedAt DEFAULT SYSUTCDATETIME();
GO
CREATE INDEX IX_Latest_UpdatedAt ON telemetry.Latest(UpdatedAt) INCLUDE (TenantId, Ts, Value, Quality);
GO

CREATE SCHEMA production;
GO

-- Standard downtime reasons shared by every company (Category drives OEE: Planned time is not counted against availability)
CREATE TABLE production.Reasons (
    ReasonCode      varchar(40)    NOT NULL CONSTRAINT PK_Reasons PRIMARY KEY,
    Name            nvarchar(100)  NOT NULL,
    Category        varchar(20)    NOT NULL CONSTRAINT CK_Reasons_Category
                                   CHECK (Category IN ('Planned','Breakdown','Changeover','Material','Quality','Operator','Utility','Other')),
    SortOrder       int            NOT NULL
);

INSERT production.Reasons (ReasonCode, Name, Category, SortOrder) VALUES
 ('planned-maintenance', N'Planned maintenance',            'Planned',    1),
 ('break',               N'Break / meal',                   'Planned',    2),
 ('no-order',            N'No order planned',               'Planned',    3),
 ('trial',               N'Trial / engineering run',        'Planned',    4),
 ('mech-failure',        N'Mechanical failure',             'Breakdown',  10),
 ('elec-failure',        N'Electrical / control failure',   'Breakdown',  11),
 ('tooling',             N'Tool / mould / die problem',     'Breakdown',  12),
 ('changeover',          N'Changeover / setup',             'Changeover', 20),
 ('no-material',         N'Waiting for material',           'Material',   30),
 ('upstream',            N'Blocked / starved by another machine', 'Material', 31),
 ('quality-check',       N'Quality check / adjustment',     'Quality',    40),
 ('no-operator',         N'No operator',                    'Operator',   50),
 ('utility',             N'Air, power or water supply',     'Utility',    60),
 ('other',               N'Other (see comment)',            'Other',      99);

CREATE TABLE production.Products (
    ProductId       uniqueidentifier NOT NULL CONSTRAINT PK_Products PRIMARY KEY CONSTRAINT DF_Products_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Products_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(150)  NOT NULL,
    IdealCycleSec   decimal(10,3)  NULL,
    Unit            nvarchar(20)   NOT NULL CONSTRAINT DF_Products_Unit DEFAULT N'pcs',
    IsActive        bit            NOT NULL CONSTRAINT DF_Products_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Products_Code UNIQUE (TenantId, Code)
);

CREATE TABLE production.WorkOrders (
    WorkOrderId     uniqueidentifier NOT NULL CONSTRAINT PK_WorkOrders PRIMARY KEY CONSTRAINT DF_WorkOrders_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_WorkOrders_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    ProductId       uniqueidentifier NOT NULL CONSTRAINT FK_WorkOrders_Product REFERENCES production.Products(ProductId),
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_WorkOrders_Asset REFERENCES plant.Assets(AssetId),
    Quantity        int            NOT NULL CONSTRAINT CK_WorkOrders_Qty CHECK (Quantity > 0),
    Status          varchar(20)    NOT NULL CONSTRAINT DF_WorkOrders_Status DEFAULT 'Planned'
                                   CONSTRAINT CK_WorkOrders_Status CHECK (Status IN ('Planned','Running','Completed','Cancelled')),
    DueDate         date           NULL,
    LotNo           varchar(60)    NULL,
    StartCount      float          NULL,   -- the machine's good-part counter when the order started
    StartScrap      float          NULL,
    GoodQty         int            NULL,
    ScrapQty        int            NULL,
    ActualStart     datetime2(0)   NULL,
    ActualEnd       datetime2(0)   NULL,
    Notes           nvarchar(500)  NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_WorkOrders_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_WorkOrders_Code UNIQUE (TenantId, Code)
);
CREATE INDEX IX_WorkOrders_Asset ON production.WorkOrders(TenantId, AssetId, Status);
-- only one running order per machine
CREATE UNIQUE INDEX UX_WorkOrders_Running ON production.WorkOrders(AssetId) WHERE Status = 'Running';

-- Opened by the worker when a machine leaves Running, closed when it runs again; operators add the reason
CREATE TABLE production.Downtimes (
    DowntimeId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Downtimes PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Downtimes_Tenant REFERENCES core.Tenants(TenantId),
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_Downtimes_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    StartTs         datetime2(3)   NOT NULL,
    EndTs           datetime2(3)   NULL,
    State           varchar(20)    NOT NULL,   -- worst state seen: Idle, Stopped, Fault, Maintenance
    ReasonCode      varchar(40)    NULL CONSTRAINT FK_Downtimes_Reason REFERENCES production.Reasons(ReasonCode),
    Comment         nvarchar(300)  NULL,
    ReasonBy        uniqueidentifier NULL,
    ReasonAt        datetime2(0)   NULL
);
CREATE INDEX IX_Downtimes_Asset ON production.Downtimes(TenantId, AssetId, StartTs);
CREATE UNIQUE INDEX UX_Downtimes_Open ON production.Downtimes(AssetId) WHERE EndTs IS NULL;
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.Products,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.Products,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.WorkOrders,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.WorkOrders,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.Downtimes,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.Downtimes;
GO
