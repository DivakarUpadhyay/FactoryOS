-- Phase 9: dashboards, scheduled reports, billing (invoices), integration API keys

-- A user's dashboard: a list of widgets with their settings (JSON). Shared dashboards are visible to the whole company.
CREATE TABLE core.Dashboards (
    DashboardId     uniqueidentifier NOT NULL CONSTRAINT PK_Dashboards PRIMARY KEY CONSTRAINT DF_Dashboards_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Dashboards_Tenant REFERENCES core.Tenants(TenantId),
    OwnerId         uniqueidentifier NOT NULL CONSTRAINT FK_Dashboards_Owner REFERENCES core.Users(UserId),
    Name            nvarchar(100)  NOT NULL,
    IsShared        bit            NOT NULL CONSTRAINT DF_Dashboards_IsShared DEFAULT 0,
    Widgets         nvarchar(max)  NOT NULL CONSTRAINT CK_Dashboards_Widgets CHECK (ISJSON(Widgets) = 1),
    UpdatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Dashboards_UpdatedAt DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Dashboards_Tenant ON core.Dashboards(TenantId, OwnerId);

-- E-mail a report's key figures every day at a local hour
CREATE TABLE core.ReportSchedules (
    ScheduleId      uniqueidentifier NOT NULL CONSTRAINT PK_ReportSchedules PRIMARY KEY CONSTRAINT DF_ReportSchedules_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_ReportSchedules_Tenant REFERENCES core.Tenants(TenantId),
    ReportType      varchar(20)    NOT NULL CONSTRAINT CK_ReportSchedules_Type CHECK (ReportType IN ('production', 'energy', 'maintenance', 'quality', 'alarms')),
    Recipients      nvarchar(500)  NOT NULL,
    LocalHour       tinyint        NOT NULL CONSTRAINT CK_ReportSchedules_Hour CHECK (LocalHour < 24),
    LastSentOn      date           NULL,
    IsActive        bit            NOT NULL CONSTRAINT DF_ReportSchedules_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_ReportSchedules_CreatedAt DEFAULT SYSUTCDATETIME()
);

-- Monthly subscription invoices (issued by the platform operator; payments are recorded by hand)
CREATE TABLE core.Invoices (
    InvoiceId       uniqueidentifier NOT NULL CONSTRAINT PK_Invoices PRIMARY KEY CONSTRAINT DF_Invoices_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Invoices_Tenant REFERENCES core.Tenants(TenantId),
    Number          varchar(30)    NOT NULL CONSTRAINT UQ_Invoices_Number UNIQUE,
    PeriodStart     date           NOT NULL,
    PeriodEnd       date           NOT NULL,
    Currency        char(3)        NOT NULL,
    Lines           nvarchar(max)  NOT NULL CONSTRAINT CK_Invoices_Lines CHECK (ISJSON(Lines) = 1),
    Subtotal        decimal(12,2)  NOT NULL,
    TaxRate         decimal(5,4)   NOT NULL,
    Tax             decimal(12,2)  NOT NULL,
    Total           decimal(12,2)  NOT NULL,
    Status          varchar(10)    NOT NULL CONSTRAINT DF_Invoices_Status DEFAULT 'Issued'
                                   CONSTRAINT CK_Invoices_Status CHECK (Status IN ('Issued', 'Paid', 'Void')),
    IssuedAt        datetime2(0)   NOT NULL CONSTRAINT DF_Invoices_IssuedAt DEFAULT SYSUTCDATETIME(),
    DueOn           date           NOT NULL,
    PaidAt          datetime2(0)   NULL,
    PaymentRef      nvarchar(100)  NULL,
    CONSTRAINT UQ_Invoices_Period UNIQUE (TenantId, PeriodStart)
);

-- Keys for other systems (ERP, BI, MES) to read the company's data over the API; stored hashed, shown once
CREATE TABLE core.ApiKeys (
    KeyHash         char(64)       NOT NULL CONSTRAINT PK_ApiKeys PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_ApiKeys_Tenant REFERENCES core.Tenants(TenantId),
    Name            nvarchar(100)  NOT NULL,
    Prefix          char(12)       NOT NULL,
    CreatedBy       uniqueidentifier NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_ApiKeys_CreatedAt DEFAULT SYSUTCDATETIME(),
    LastUsedAt      datetime2(0)   NULL,
    RevokedAt       datetime2(0)   NULL
);
CREATE INDEX IX_ApiKeys_Tenant ON core.ApiKeys(TenantId);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.Dashboards,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.Dashboards,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.ReportSchedules,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.ReportSchedules,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.Invoices,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.Invoices,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.ApiKeys,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.ApiKeys;
GO
