-- Divakar FactoryOS 0001: platform core (plans, tenants, users, roles, permissions, tokens, audit)
-- Every tenant-owned table carries TenantId; row-level security (0003) keeps tenants apart.

CREATE SCHEMA core;
GO
CREATE SCHEMA plant;
GO

CREATE TABLE core.Plans (
    PlanCode        varchar(40)    NOT NULL CONSTRAINT PK_Plans PRIMARY KEY,
    Name            nvarchar(100)  NOT NULL,
    PriceMonthly    decimal(12,2)  NOT NULL,
    Currency        char(3)        NOT NULL,
    MaxPlants       int            NOT NULL,
    MaxAssets       int            NOT NULL,
    MaxUsers        int            NOT NULL,
    Modules         nvarchar(max)  NOT NULL CONSTRAINT CK_Plans_Modules CHECK (ISJSON(Modules) = 1),
    IsActive        bit            NOT NULL CONSTRAINT DF_Plans_IsActive DEFAULT 1,
    SortOrder       int            NOT NULL CONSTRAINT DF_Plans_SortOrder DEFAULT 0
);

CREATE TABLE core.Tenants (
    TenantId        uniqueidentifier NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY CONSTRAINT DF_Tenants_Id DEFAULT NEWSEQUENTIALID(),
    Code            varchar(40)    NOT NULL CONSTRAINT UQ_Tenants_Code UNIQUE,
    Name            nvarchar(200)  NOT NULL,
    PlanCode        varchar(40)    NOT NULL CONSTRAINT FK_Tenants_Plan REFERENCES core.Plans(PlanCode),
    Status          varchar(20)    NOT NULL CONSTRAINT CK_Tenants_Status CHECK (Status IN ('Trial','Active','Suspended','Cancelled')),
    Country         char(2)        NOT NULL CONSTRAINT DF_Tenants_Country DEFAULT 'TW',
    TimeZone        varchar(64)    NOT NULL CONSTRAINT DF_Tenants_TimeZone DEFAULT 'Asia/Taipei',
    Currency        char(3)        NOT NULL CONSTRAINT DF_Tenants_Currency DEFAULT 'TWD',
    TrialEndsAt     datetime2(0)   NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Tenants_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE core.Users (
    UserId          uniqueidentifier NOT NULL CONSTRAINT PK_Users PRIMARY KEY CONSTRAINT DF_Users_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NULL CONSTRAINT FK_Users_Tenant REFERENCES core.Tenants(TenantId),   -- NULL = platform operator
    Email           nvarchar(256)  NOT NULL,
    DisplayName     nvarchar(150)  NOT NULL,
    PasswordHash    nvarchar(500)  NOT NULL,
    IsSuperAdmin    bit            NOT NULL CONSTRAINT DF_Users_IsSuperAdmin DEFAULT 0,
    IsActive        bit            NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1,
    FailedLogins    int            NOT NULL CONSTRAINT DF_Users_FailedLogins DEFAULT 0,
    LockedUntil     datetime2(0)   NULL,
    LastLoginAt     datetime2(0)   NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Users_Email UNIQUE (Email),
    CONSTRAINT CK_Users_Scope CHECK ((IsSuperAdmin = 1 AND TenantId IS NULL) OR (IsSuperAdmin = 0 AND TenantId IS NOT NULL))
);

CREATE TABLE core.Permissions (
    PermissionCode  varchar(60)    NOT NULL CONSTRAINT PK_Permissions PRIMARY KEY,
    Module          varchar(40)    NOT NULL,
    Description     nvarchar(200)  NOT NULL
);

CREATE TABLE core.Roles (
    RoleId          uniqueidentifier NOT NULL CONSTRAINT PK_Roles PRIMARY KEY CONSTRAINT DF_Roles_Id DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Roles_Tenant REFERENCES core.Tenants(TenantId),
    Code            varchar(40)    NOT NULL,
    Name            nvarchar(100)  NOT NULL,
    IsSystem        bit            NOT NULL CONSTRAINT DF_Roles_IsSystem DEFAULT 0,
    CONSTRAINT UQ_Roles_Code UNIQUE (TenantId, Code)
);

CREATE TABLE core.RolePermissions (
    RoleId          uniqueidentifier NOT NULL CONSTRAINT FK_RolePermissions_Role REFERENCES core.Roles(RoleId) ON DELETE CASCADE,
    PermissionCode  varchar(60)    NOT NULL CONSTRAINT FK_RolePermissions_Permission REFERENCES core.Permissions(PermissionCode),
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, PermissionCode)
);

CREATE TABLE core.UserRoles (
    UserId          uniqueidentifier NOT NULL CONSTRAINT FK_UserRoles_User REFERENCES core.Users(UserId) ON DELETE CASCADE,
    RoleId          uniqueidentifier NOT NULL CONSTRAINT FK_UserRoles_Role REFERENCES core.Roles(RoleId),
    CONSTRAINT PK_UserRoles PRIMARY KEY (UserId, RoleId)
);

-- Refresh tokens are stored hashed; a used token is replaced (rotation) and reuse of an old one revokes the family.
CREATE TABLE core.RefreshTokens (
    TokenHash       char(64)       NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
    UserId          uniqueidentifier NOT NULL CONSTRAINT FK_RefreshTokens_User REFERENCES core.Users(UserId) ON DELETE CASCADE,
    FamilyId        uniqueidentifier NOT NULL,
    ExpiresAt       datetime2(0)   NOT NULL,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_RefreshTokens_CreatedAt DEFAULT SYSUTCDATETIME(),
    RevokedAt       datetime2(0)   NULL,
    ReplacedByHash  char(64)       NULL
);
CREATE INDEX IX_RefreshTokens_Family ON core.RefreshTokens(FamilyId);

CREATE TABLE core.AuditLog (
    AuditId         bigint IDENTITY NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    TenantId        uniqueidentifier NULL,
    UserId          uniqueidentifier NULL,
    Action          varchar(60)    NOT NULL,
    Entity          varchar(60)    NOT NULL,
    EntityId        nvarchar(100)  NULL,
    Detail          nvarchar(max)  NULL,
    IpAddress       varchar(45)    NULL,
    At              datetime2(3)   NOT NULL CONSTRAINT DF_AuditLog_At DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_AuditLog_Tenant_At ON core.AuditLog(TenantId, At DESC);
