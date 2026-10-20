-- Password reset links and two-step sign-in (TOTP authenticator apps).

ALTER TABLE core.Users ADD
    TotpSecret      nvarchar(400)  NULL,   -- encrypted (enc:v1:); set while enrolling, kept once enabled
    TotpEnabled     bit            NOT NULL CONSTRAINT DF_Users_TotpEnabled DEFAULT 0,
    TotpLastStep    bigint         NULL,   -- last accepted 30-second step, so a code cannot be used twice
    RecoveryCodes   nvarchar(1000) NULL,   -- JSON array of SHA-256 hashes of unused recovery codes
    PasswordChangedAt datetime2(0) NULL;
GO

-- one-time tokens: password reset links and the second step of a sign-in; only hashes are stored
CREATE TABLE core.AuthTokens (
    TokenHash       char(64)       NOT NULL CONSTRAINT PK_AuthTokens PRIMARY KEY,
    UserId          uniqueidentifier NOT NULL CONSTRAINT FK_AuthTokens_User REFERENCES core.Users(UserId) ON DELETE CASCADE,
    Purpose         varchar(10)    NOT NULL CONSTRAINT CK_AuthTokens_Purpose CHECK (Purpose IN ('reset', 'mfa')),
    ExpiresAt       datetime2(0)   NOT NULL,
    UsedAt          datetime2(0)   NULL,
    Attempts        int            NOT NULL CONSTRAINT DF_AuthTokens_Attempts DEFAULT 0,
    CreatedAt       datetime2(0)   NOT NULL CONSTRAINT DF_AuthTokens_CreatedAt DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_AuthTokens_User ON core.AuthTokens(UserId, Purpose);
