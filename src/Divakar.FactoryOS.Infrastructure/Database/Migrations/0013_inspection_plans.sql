-- Inspection plans: what to check on a product, when (first piece, every N parts, final), and the recorded inspections

CREATE TABLE quality.InspectionPlans (
    PlanId          uniqueidentifier NOT NULL CONSTRAINT PK_InspPlans PRIMARY KEY CONSTRAINT DF_InspPlans_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_InspPlans_Tenant REFERENCES core.Tenants(TenantId),
    ProductId       uniqueidentifier NOT NULL CONSTRAINT FK_InspPlans_Product REFERENCES production.Products(ProductId),
    Name            nvarchar(150)  NOT NULL,
    TriggerType     varchar(12)    NOT NULL CONSTRAINT CK_InspPlans_Trigger CHECK (TriggerType IN ('FirstPiece', 'EveryN', 'Final')),
    EveryN          int            NULL CONSTRAINT CK_InspPlans_EveryN CHECK (EveryN IS NULL OR EveryN > 0),
    CreateNcrOnFail bit            NOT NULL CONSTRAINT DF_InspPlans_Ncr DEFAULT 1,
    IsActive        bit            NOT NULL CONSTRAINT DF_InspPlans_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_InspPlans_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_InspPlans_N CHECK (TriggerType <> 'EveryN' OR EveryN IS NOT NULL)
);

-- A check is either measured against a characteristic's specification limits, or judged pass/fail
CREATE TABLE quality.InspectionItems (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_InspItems_Tenant REFERENCES core.Tenants(TenantId),
    PlanId          uniqueidentifier NOT NULL CONSTRAINT FK_InspItems_Plan REFERENCES quality.InspectionPlans(PlanId) ON DELETE CASCADE,
    Seq             int            NOT NULL,
    Text            nvarchar(200)  NOT NULL,
    CharId          uniqueidentifier NULL CONSTRAINT FK_InspItems_Char REFERENCES quality.Characteristics(CharId),
    CONSTRAINT PK_InspItems PRIMARY KEY (PlanId, Seq)
);

CREATE TABLE quality.Inspections (
    InspectionId    uniqueidentifier NOT NULL CONSTRAINT PK_Inspections PRIMARY KEY CONSTRAINT DF_Inspections_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Inspections_Tenant REFERENCES core.Tenants(TenantId),
    PlanId          uniqueidentifier NOT NULL CONSTRAINT FK_Inspections_Plan REFERENCES quality.InspectionPlans(PlanId),
    WorkOrderId     uniqueidentifier NULL CONSTRAINT FK_Inspections_Wo REFERENCES production.WorkOrders(WorkOrderId),
    LotNo           varchar(60)    NULL,
    Result          varchar(4)     NOT NULL CONSTRAINT CK_Inspections_Result CHECK (Result IN ('Pass', 'Fail')),
    NcId            uniqueidentifier NULL CONSTRAINT FK_Inspections_Ncr REFERENCES quality.NonConformances(NcId),
    Note            nvarchar(300)  NULL,
    InspectedBy     uniqueidentifier NULL,
    At              datetime2(0)   NOT NULL CONSTRAINT DF_Inspections_At DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Inspections_Wo ON quality.Inspections(TenantId, WorkOrderId, PlanId);

CREATE TABLE quality.InspectionResults (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_InspResults_Tenant REFERENCES core.Tenants(TenantId),
    InspectionId    uniqueidentifier NOT NULL CONSTRAINT FK_InspResults_Insp REFERENCES quality.Inspections(InspectionId) ON DELETE CASCADE,
    Seq             int            NOT NULL,
    Value           float          NULL,
    Passed          bit            NOT NULL,
    CONSTRAINT PK_InspResults PRIMARY KEY (InspectionId, Seq)
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.InspectionPlans,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.InspectionPlans,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.InspectionItems,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.InspectionItems,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.Inspections,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.Inspections,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON quality.InspectionResults,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON quality.InspectionResults;
GO
