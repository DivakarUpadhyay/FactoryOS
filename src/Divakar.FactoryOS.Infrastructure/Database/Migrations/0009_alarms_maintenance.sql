-- Phase 7: rules engine and alarms, predictive anomalies, preventive maintenance, technician work orders, notifications

CREATE SCHEMA alarms;
GO
CREATE SCHEMA maintenance;
GO

-- "If <sensor> on <asset or asset type> is <op> <threshold> for <duration> then raise <severity>"
CREATE TABLE alarms.Rules (
    RuleId          uniqueidentifier NOT NULL CONSTRAINT PK_Rules PRIMARY KEY CONSTRAINT DF_Rules_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Rules_Tenant REFERENCES core.Tenants(TenantId),
    Name            nvarchar(150)  NOT NULL,
    SensorTypeCode  varchar(40)    NOT NULL CONSTRAINT FK_Rules_Sensor REFERENCES plant.SensorTypes(SensorTypeCode),
    AssetId         uniqueidentifier NULL CONSTRAINT FK_Rules_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,   -- one asset, or
    AssetTypeCode   varchar(40)    NULL CONSTRAINT FK_Rules_AssetType REFERENCES plant.AssetTypes(AssetTypeCode),        -- every asset of a type, or all when both are empty
    Operator        varchar(2)     NOT NULL CONSTRAINT CK_Rules_Operator CHECK (Operator IN ('>', '>=', '<', '<=', '=', '!=')),
    Threshold       float          NOT NULL,
    DurationSec     int            NOT NULL CONSTRAINT DF_Rules_Duration DEFAULT 0 CONSTRAINT CK_Rules_Duration CHECK (DurationSec BETWEEN 0 AND 86400),
    Severity        varchar(10)    NOT NULL CONSTRAINT CK_Rules_Severity CHECK (Severity IN ('Info', 'Warning', 'Critical')),
    Notify          bit            NOT NULL CONSTRAINT DF_Rules_Notify DEFAULT 1,
    CreateWorkOrder bit            NOT NULL CONSTRAINT DF_Rules_CreateWo DEFAULT 0,
    IsActive        bit            NOT NULL CONSTRAINT DF_Rules_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Rules_CreatedAt DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Rules_Tenant ON alarms.Rules(TenantId, IsActive);

-- Since when a rule's condition has been true on a tag (survives worker restarts)
CREATE TABLE alarms.Pending (
    RuleId          uniqueidentifier NOT NULL CONSTRAINT FK_Pending_Rule REFERENCES alarms.Rules(RuleId) ON DELETE CASCADE,
    TagId           uniqueidentifier NOT NULL,
    Since           datetime2(3)   NOT NULL,
    CONSTRAINT PK_Pending PRIMARY KEY (RuleId, TagId)
);

CREATE TABLE alarms.Alarms (
    AlarmId         bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Alarms PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Alarms_Tenant REFERENCES core.Tenants(TenantId),
    Kind            varchar(10)    NOT NULL CONSTRAINT CK_Alarms_Kind CHECK (Kind IN ('Rule', 'Anomaly')),
    RuleId          uniqueidentifier NULL CONSTRAINT FK_Alarms_Rule REFERENCES alarms.Rules(RuleId),   -- detached by the app when a rule is deleted
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_Alarms_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    TagId           uniqueidentifier NULL,
    Severity        varchar(10)    NOT NULL,
    Message         nvarchar(300)  NOT NULL,
    Value           float          NULL,
    State           varchar(15)    NOT NULL CONSTRAINT DF_Alarms_State DEFAULT 'Active'
                                   CONSTRAINT CK_Alarms_State CHECK (State IN ('Active', 'Acknowledged', 'Cleared')),
    RaisedAt        datetime2(3)   NOT NULL CONSTRAINT DF_Alarms_RaisedAt DEFAULT SYSUTCDATETIME(),
    AckAt           datetime2(0)   NULL,
    AckBy           uniqueidentifier NULL,
    AckComment      nvarchar(300)  NULL,
    ClearedAt       datetime2(3)   NULL,
    EscalatedAt     datetime2(0)   NULL
);
CREATE INDEX IX_Alarms_Tenant ON alarms.Alarms(TenantId, State, RaisedAt);
-- at most one open alarm per rule and tag, and per anomaly tag
CREATE UNIQUE INDEX UX_Alarms_OpenRule ON alarms.Alarms(RuleId, TagId) WHERE ClearedAt IS NULL AND RuleId IS NOT NULL;
CREATE UNIQUE INDEX UX_Alarms_OpenAnomaly ON alarms.Alarms(TagId) WHERE ClearedAt IS NULL AND Kind = 'Anomaly';

CREATE TABLE maintenance.Plans (
    PlanId          uniqueidentifier NOT NULL CONSTRAINT PK_MaintPlans PRIMARY KEY CONSTRAINT DF_MaintPlans_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_MaintPlans_Tenant REFERENCES core.Tenants(TenantId),
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_MaintPlans_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    Name            nvarchar(150)  NOT NULL,
    TriggerType     varchar(10)    NOT NULL CONSTRAINT CK_MaintPlans_Trigger CHECK (TriggerType IN ('Calendar', 'RunHours', 'Cycles')),
    Interval        int            NOT NULL CONSTRAINT CK_MaintPlans_Interval CHECK (Interval > 0),   -- days, running hours or cycles
    Checklist       nvarchar(max)  NULL CONSTRAINT CK_MaintPlans_Checklist CHECK (Checklist IS NULL OR ISJSON(Checklist) = 1),
    EstimatedMinutes int           NULL,
    LastDoneAt      datetime2(0)   NOT NULL CONSTRAINT DF_MaintPlans_LastDone DEFAULT SYSUTCDATETIME(),
    LastDoneCount   float          NULL,   -- the cycle counter at the last service
    IsActive        bit            NOT NULL CONSTRAINT DF_MaintPlans_IsActive DEFAULT 1,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_MaintPlans_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE maintenance.WorkOrders (
    MaintWoId       uniqueidentifier NOT NULL CONSTRAINT PK_MaintWo PRIMARY KEY CONSTRAINT DF_MaintWo_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_MaintWo_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    AssetId         uniqueidentifier NOT NULL CONSTRAINT FK_MaintWo_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    PlanId          uniqueidentifier NULL CONSTRAINT FK_MaintWo_Plan REFERENCES maintenance.Plans(PlanId),
    AlarmId         bigint         NULL CONSTRAINT FK_MaintWo_Alarm REFERENCES alarms.Alarms(AlarmId),
    Type            varchar(12)    NOT NULL CONSTRAINT CK_MaintWo_Type CHECK (Type IN ('Preventive', 'Corrective', 'Predictive')),
    Priority        tinyint        NOT NULL CONSTRAINT DF_MaintWo_Priority DEFAULT 2 CONSTRAINT CK_MaintWo_Priority CHECK (Priority BETWEEN 1 AND 3),
    Title           nvarchar(200)  NOT NULL,
    Description     nvarchar(1000) NULL,
    Status          varchar(12)    NOT NULL CONSTRAINT DF_MaintWo_Status DEFAULT 'Open'
                                   CONSTRAINT CK_MaintWo_Status CHECK (Status IN ('Open', 'InProgress', 'Done', 'Cancelled')),
    AssignedTo      uniqueidentifier NULL CONSTRAINT FK_MaintWo_User REFERENCES core.Users(UserId),
    Checklist       nvarchar(max)  NULL CONSTRAINT CK_MaintWo_Checklist CHECK (Checklist IS NULL OR ISJSON(Checklist) = 1),
    DueAt           datetime2(0)   NULL,
    StartedAt       datetime2(0)   NULL,
    CompletedAt     datetime2(0)   NULL,
    Notes           nvarchar(1000) NULL,
    CreatedBy       uniqueidentifier NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_MaintWo_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_MaintWo_Code UNIQUE (TenantId, Code)
);
CREATE INDEX IX_MaintWo_Tenant ON maintenance.WorkOrders(TenantId, Status, DueAt);
-- one open preventive order per plan
CREATE UNIQUE INDEX UX_MaintWo_OpenPlan ON maintenance.WorkOrders(PlanId) WHERE PlanId IS NOT NULL AND Status IN ('Open', 'InProgress');

CREATE TABLE core.Notifications (
    NotificationId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Notifications PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Notifications_Tenant REFERENCES core.Tenants(TenantId),
    Channel         varchar(10)    NOT NULL CONSTRAINT CK_Notifications_Channel CHECK (Channel IN ('Email', 'Webhook')),
    Recipient       nvarchar(500)  NOT NULL,
    Subject         nvarchar(200)  NOT NULL,
    Body            nvarchar(2000) NOT NULL,
    Status          varchar(10)    NOT NULL CONSTRAINT DF_Notifications_Status DEFAULT 'Pending'
                                   CONSTRAINT CK_Notifications_Status CHECK (Status IN ('Pending', 'Sent', 'Failed', 'Skipped')),
    Attempts        int            NOT NULL CONSTRAINT DF_Notifications_Attempts DEFAULT 0,
    Error           nvarchar(300)  NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Notifications_CreatedAt DEFAULT SYSUTCDATETIME(),
    SentAt          datetime2(0)   NULL
);
CREATE INDEX IX_Notifications_Status ON core.Notifications(Status, CreatedAt);
CREATE INDEX IX_Notifications_Tenant ON core.Notifications(TenantId, CreatedAt);

-- A webhook receives every alarm as JSON (connect LINE, Teams, Slack or your own system through it)
ALTER TABLE core.TenantSettings ADD AlertWebhookUrl nvarchar(500) NULL;
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON alarms.Rules,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON alarms.Rules,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON alarms.Alarms,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON alarms.Alarms,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON maintenance.Plans,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON maintenance.Plans,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON maintenance.WorkOrders,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON maintenance.WorkOrders,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.Notifications,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.Notifications;
GO
