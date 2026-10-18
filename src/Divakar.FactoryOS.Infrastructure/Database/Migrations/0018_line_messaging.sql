-- LINE Messaging API: alarm messages to LINE users and groups (common on Taiwanese and Japanese shop floors).

ALTER TABLE core.Notifications DROP CONSTRAINT CK_Notifications_Channel;
ALTER TABLE core.Notifications ADD CONSTRAINT CK_Notifications_Channel CHECK (Channel IN ('Email', 'Webhook', 'Line'));

CREATE TABLE core.LineChannels (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT PK_LineChannels PRIMARY KEY CONSTRAINT FK_LineChannels_Tenant REFERENCES core.Tenants(TenantId),
    ChannelSecret   nvarchar(400)  NOT NULL,   -- encrypted (enc:v1:)
    AccessToken     nvarchar(1200) NOT NULL,   -- encrypted (enc:v1:)
    Recipients      nvarchar(1000) NULL,       -- comma-separated LINE user / group / room ids
    Enabled         bit            NOT NULL CONSTRAINT DF_LineChannels_Enabled DEFAULT 1,
    UpdatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_LineChannels_UpdatedAt DEFAULT SYSUTCDATETIME()
);

-- who has talked to the bot (from the webhook), so an admin can pick recipients instead of typing ids
CREATE TABLE core.LineContacts (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_LineContacts_Tenant REFERENCES core.Tenants(TenantId),
    SourceId        varchar(64)    NOT NULL,
    Kind            varchar(10)    NOT NULL CONSTRAINT CK_LineContacts_Kind CHECK (Kind IN ('user', 'group', 'room')),
    LastText        nvarchar(100)  NULL,
    LastSeen        datetime2(0)   NOT NULL CONSTRAINT DF_LineContacts_LastSeen DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_LineContacts PRIMARY KEY (TenantId, SourceId)
);
GO

ALTER SECURITY POLICY sec.TenantIsolation
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.LineChannels,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.LineChannels,
    ADD FILTER PREDICATE sec.fn_TenantAccess(TenantId) ON core.LineContacts,
    ADD BLOCK  PREDICATE sec.fn_TenantAccess(TenantId) ON core.LineContacts;
