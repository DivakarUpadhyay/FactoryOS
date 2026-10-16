-- Bill of materials, routing, and work-order operations

-- What one unit of a product consumes (inventory items), with an allowance for scrap
CREATE TABLE production.BomLines (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_BomLines_Tenant REFERENCES core.Tenants(TenantId),
    ProductId       uniqueidentifier NOT NULL CONSTRAINT FK_BomLines_Product REFERENCES production.Products(ProductId) ON DELETE CASCADE,
    ItemId          uniqueidentifier NOT NULL CONSTRAINT FK_BomLines_Item REFERENCES inventory.Items(ItemId),
    QtyPer          decimal(14,5)  NOT NULL CONSTRAINT CK_BomLines_Qty CHECK (QtyPer > 0),
    ScrapPct        decimal(5,2)   NOT NULL CONSTRAINT DF_BomLines_Scrap DEFAULT 0 CONSTRAINT CK_BomLines_Scrap CHECK (ScrapPct BETWEEN 0 AND 100),
    CONSTRAINT PK_BomLines PRIMARY KEY (ProductId, ItemId)
);

-- The operations a product goes through, in order, and where
CREATE TABLE production.RoutingSteps (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Routing_Tenant REFERENCES core.Tenants(TenantId),
    ProductId       uniqueidentifier NOT NULL CONSTRAINT FK_Routing_Product REFERENCES production.Products(ProductId) ON DELETE CASCADE,
    Seq             int            NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    AssetId         uniqueidentifier NULL CONSTRAINT FK_Routing_Asset REFERENCES plant.Assets(AssetId),
    StdCycleSec     decimal(10,3)  NULL,
    SetupMinutes    int            NULL,
    CONSTRAINT PK_RoutingSteps PRIMARY KEY (ProductId, Seq)
);

-- A work order's copy of the routing, so later routing changes do not rewrite orders in progress
CREATE TABLE production.WorkOrderSteps (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_WoSteps_Tenant REFERENCES core.Tenants(TenantId),
    WorkOrderId     uniqueidentifier NOT NULL CONSTRAINT FK_WoSteps_Wo REFERENCES production.WorkOrders(WorkOrderId) ON DELETE CASCADE,
    Seq             int            NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    AssetId         uniqueidentifier NULL CONSTRAINT FK_WoSteps_Asset REFERENCES plant.Assets(AssetId),
    StdCycleSec     decimal(10,3)  NULL,
    Status          varchar(10)    NOT NULL CONSTRAINT DF_WoSteps_Status DEFAULT 'Pending'
                                   CONSTRAINT CK_WoSteps_Status CHECK (Status IN ('Pending', 'Running', 'Done')),
    GoodQty         int            NULL,
    ScrapQty        int            NULL,
    StartedAt       datetime2(0)   NULL,
    DoneAt          datetime2(0)   NULL,
    CONSTRAINT PK_WorkOrderSteps PRIMARY KEY (WorkOrderId, Seq)
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.BomLines,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.BomLines,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.RoutingSteps,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.RoutingSteps,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON production.WorkOrderSteps,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON production.WorkOrderSteps;
GO
