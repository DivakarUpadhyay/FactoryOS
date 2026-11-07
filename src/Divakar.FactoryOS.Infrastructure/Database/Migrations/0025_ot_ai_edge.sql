-- OT-layer AI and the edge agent.
-- ai.Insights: what the detectors found (energy anomalies, drift, frozen sensors, time to a limit, idle waste), from the cloud
-- worker or from an edge agent next to the machines. Operators mark each one real or false; ai.TagTuning keeps the resulting
-- sensitivity per tag and detector, so false alarms on a tag make that detector less eager there.
-- plant.EdgeAgents: a small program on a PC or gateway in the plant that reads the machines itself, keeps readings on disk while
-- the link is down, and forwards them; the cloud worker then leaves its assets alone.

CREATE SCHEMA ai;
GO

CREATE TABLE ai.Insights (
    InsightId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Insights PRIMARY KEY,
    TenantId      uniqueidentifier NOT NULL CONSTRAINT FK_Insights_Tenant REFERENCES core.Tenants(TenantId),
    AssetId       uniqueidentifier NOT NULL CONSTRAINT FK_Insights_Asset REFERENCES plant.Assets(AssetId) ON DELETE CASCADE,
    TagId         uniqueidentifier NULL,
    Kind          varchar(20)    NOT NULL CONSTRAINT CK_Insights_Kind
                                 CHECK (Kind IN ('EnergyAnomaly', 'MeterFlat', 'Drift', 'Frozen', 'TimeToLimit', 'IdleWaste', 'Spike')),
    Severity      varchar(10)    NOT NULL CONSTRAINT CK_Insights_Severity CHECK (Severity IN ('Info', 'Warning', 'Critical')),
    Title         nvarchar(200)  NOT NULL,
    Detail        nvarchar(1000) NULL,
    Score         float          NOT NULL,
    Evidence      nvarchar(max)  NULL CONSTRAINT CK_Insights_Evidence CHECK (Evidence IS NULL OR ISJSON(Evidence) = 1),
    Source        nvarchar(60)   NOT NULL,      -- cloud, or edge:<agent name>
    DetectedAt    datetime2(0)   NOT NULL,
    LastSeenAt    datetime2(0)   NOT NULL,
    ClosedAt      datetime2(0)   NULL,
    Feedback      varchar(5)     NULL CONSTRAINT CK_Insights_Feedback CHECK (Feedback IN ('Real', 'False')),
    FeedbackBy    uniqueidentifier NULL,
    FeedbackAt    datetime2(0)   NULL,
    FeedbackNote  nvarchar(300)  NULL
);
CREATE INDEX IX_Insights_Open ON ai.Insights(TenantId, ClosedAt, DetectedAt DESC);
CREATE INDEX IX_Insights_Key ON ai.Insights(AssetId, Kind, TagId) WHERE ClosedAt IS NULL;

CREATE TABLE ai.TagTuning (
    TenantId      uniqueidentifier NOT NULL CONSTRAINT FK_TagTuning_Tenant REFERENCES core.Tenants(TenantId),
    TagKey        uniqueidentifier NOT NULL,    -- the tag, or the asset for detectors that work on a whole asset
    Kind          varchar(20)    NOT NULL,
    Sensitivity   float          NOT NULL CONSTRAINT DF_TagTuning_Sens DEFAULT 1,   -- multiplies the detector's threshold
    RealCount     int            NOT NULL CONSTRAINT DF_TagTuning_Real DEFAULT 0,
    FalseCount    int            NOT NULL CONSTRAINT DF_TagTuning_False DEFAULT 0,
    UpdatedAt     datetime2(0)   NOT NULL CONSTRAINT DF_TagTuning_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_TagTuning PRIMARY KEY (TagKey, Kind)
);

CREATE TABLE plant.EdgeAgents (
    AgentId       uniqueidentifier NOT NULL CONSTRAINT PK_EdgeAgents PRIMARY KEY CONSTRAINT DF_EdgeAgents_Id DEFAULT NEWSEQUENTIALID(),
    TenantId      uniqueidentifier NOT NULL CONSTRAINT FK_EdgeAgents_Tenant REFERENCES core.Tenants(TenantId),
    Name          nvarchar(100)  NOT NULL,
    KeyHash       varchar(100)   NOT NULL,
    KeyPrefix     varchar(12)    NOT NULL,
    IsActive      bit            NOT NULL CONSTRAINT DF_EdgeAgents_Active DEFAULT 1,
    Version       varchar(40)    NULL,
    Hostname      nvarchar(100)  NULL,
    Buffered      bigint         NULL,          -- readings waiting on the agent's disk at its last heartbeat
    Dropped       bigint         NULL,          -- readings it had to drop because its buffer was full
    LastError     nvarchar(500)  NULL,
    LastSeenAt    datetime2(0)   NULL,
    CreatedAt     datetime2(0)   NOT NULL CONSTRAINT DF_EdgeAgents_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_EdgeAgents_Name UNIQUE (TenantId, Name)
);
CREATE UNIQUE INDEX UX_EdgeAgents_Key ON plant.EdgeAgents(KeyHash);

CREATE TABLE plant.EdgeAgentAssets (
    TenantId      uniqueidentifier NOT NULL CONSTRAINT FK_EdgeAgentAssets_Tenant REFERENCES core.Tenants(TenantId),
    AgentId       uniqueidentifier NOT NULL CONSTRAINT FK_EdgeAgentAssets_Agent REFERENCES plant.EdgeAgents(AgentId) ON DELETE CASCADE,
    AssetId       uniqueidentifier NOT NULL CONSTRAINT FK_EdgeAgentAssets_Asset REFERENCES plant.Assets(AssetId),
    CONSTRAINT PK_EdgeAgentAssets PRIMARY KEY (AgentId, AssetId)
);
CREATE UNIQUE INDEX UX_EdgeAgentAssets_Asset ON plant.EdgeAgentAssets(AssetId);   -- one agent reads an asset
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON ai.Insights,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON ai.Insights,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON ai.TagTuning,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON ai.TagTuning,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.EdgeAgents,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.EdgeAgents,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON plant.EdgeAgentAssets,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON plant.EdgeAgentAssets;
GO

-- Native drivers for the protocols that were planned
UPDATE plant.Protocols SET SupportLevel = 'Native', Notes = N'Host application: decodes NBIRTH/DBIRTH/NDATA/DDATA protobuf payloads, metric aliases, requests rebirth when an alias is unknown'
WHERE ProtocolCode = 'sparkplugb';
UPDATE plant.Protocols SET SupportLevel = 'Native', Notes = N'Subscribes to ROS 2 topics through rosbridge (WebSocket JSON); JSON paths into the message'
WHERE ProtocolCode = 'ros2';
UPDATE plant.Protocols SET SupportLevel = 'Native', Notes = N'RTDE protocol v2 output recipe (robot mode, joint temperatures, TCP speed, currents…)'
WHERE ProtocolCode = 'ur-rtde';
UPDATE plant.Protocols SET SupportLevel = 'Native', Notes = N'HSMS-SS active mode (SEMI E37), S1F13 establish communication, S1F3/S1F4 status variables by SVID (SEMI E5/E30)'
WHERE ProtocolCode = 'secs-gem';
GO
