/* =====================================================================
   002_Extensions.sql
   Extends the 001 schema with the concepts required by the assessment
   that were missing from the initial script:
     - EncryptionProfiles (key REFERENCES only, never key material)
     - Operations.EncryptionProfileId / MaxResponseBytes / UpdatedBy
     - ParameterRules.MinLength
     - HeaderRules (explicit forwarding allowlist)
     - Configuration history via SQL Server system-versioned temporal tables
   The script is idempotent: it can be executed more than once.
   ===================================================================== */
USE SecureProxyDb;
GO

/* ---------- 1. Encryption profiles ---------- */
IF OBJECT_ID(N'dbo.EncryptionProfiles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EncryptionProfiles (
        Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EncryptionProfiles PRIMARY KEY,
        Name          NVARCHAR(100) NOT NULL CONSTRAINT UQ_EncryptionProfiles_Name UNIQUE,
        Algorithm     VARCHAR(20)   NOT NULL CONSTRAINT DF_EncryptionProfiles_Alg DEFAULT ('AES-256-GCM')
                      CONSTRAINT CK_EncryptionProfiles_Alg CHECK (Algorithm IN ('AES-256-GCM')),
        -- Name of the secret in the key source (user-secrets / env / Key Vault). NOT the key itself.
        KeyReference  NVARCHAR(100) NOT NULL,
        KeyVersion    INT           NOT NULL CONSTRAINT DF_EncryptionProfiles_KeyVersion DEFAULT (1)
                      CONSTRAINT CK_EncryptionProfiles_KeyVersion CHECK (KeyVersion >= 1),
        IsEnabled     BIT           NOT NULL CONSTRAINT DF_EncryptionProfiles_IsEnabled DEFAULT (1),
        CreatedAt     DATETIME2(3)  NOT NULL CONSTRAINT DF_EncryptionProfiles_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt     DATETIME2(3)  NOT NULL CONSTRAINT DF_EncryptionProfiles_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy     NVARCHAR(100) NULL,
        RowVersion    ROWVERSION    NOT NULL
    );
END
GO

/* ---------- 2. Operations extensions ---------- */
IF COL_LENGTH(N'dbo.Operations', N'EncryptionProfileId') IS NULL
    ALTER TABLE dbo.Operations ADD EncryptionProfileId INT NULL
        CONSTRAINT FK_Operations_EncryptionProfiles REFERENCES dbo.EncryptionProfiles(Id);
GO
IF COL_LENGTH(N'dbo.Operations', N'MaxResponseBytes') IS NULL
    ALTER TABLE dbo.Operations ADD MaxResponseBytes INT NOT NULL
        CONSTRAINT DF_Operations_MaxResp DEFAULT (1048576);
GO
IF COL_LENGTH(N'dbo.Operations', N'UpdatedBy') IS NULL
    ALTER TABLE dbo.Operations ADD UpdatedBy NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'dbo.Destinations', N'UpdatedBy') IS NULL
    ALTER TABLE dbo.Destinations ADD UpdatedBy NVARCHAR(100) NULL;
GO
IF OBJECT_ID(N'dbo.CK_Operations_Encryption', N'C') IS NULL
    ALTER TABLE dbo.Operations ADD CONSTRAINT CK_Operations_Encryption
        CHECK (RequiresEncryption = 0 OR EncryptionProfileId IS NOT NULL);
GO
IF OBJECT_ID(N'dbo.CK_Operations_Sizes', N'C') IS NULL
    ALTER TABLE dbo.Operations ADD CONSTRAINT CK_Operations_Sizes
        CHECK (MaxRequestBytes BETWEEN 1 AND 1048576 AND MaxResponseBytes BETWEEN 1 AND 104857600);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Operations_EncryptionProfileId')
    CREATE INDEX IX_Operations_EncryptionProfileId ON dbo.Operations(EncryptionProfileId);
GO

/* ---------- 3. ParameterRules extensions ---------- */
IF COL_LENGTH(N'dbo.ParameterRules', N'MinLength') IS NULL
    ALTER TABLE dbo.ParameterRules ADD MinLength INT NULL;
GO
-- System-versioned (temporal) tables do not accept FKs with cascading actions.
-- Recreate the 001 FK without ON DELETE CASCADE; configuration rows are never
-- hard-deleted by the proxy (operations are disabled instead), and any manual
-- delete must remove child rules explicitly - which also leaves an audit trail.
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ParameterRules_Operations' AND delete_referential_action <> 0)
BEGIN
    ALTER TABLE dbo.ParameterRules DROP CONSTRAINT FK_ParameterRules_Operations;
    ALTER TABLE dbo.ParameterRules ADD CONSTRAINT FK_ParameterRules_Operations
        FOREIGN KEY (OperationId) REFERENCES dbo.Operations(Id);
END
GO

/* ---------- 4. Header forwarding allowlist ---------- */
IF OBJECT_ID(N'dbo.HeaderRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HeaderRules (
        Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HeaderRules PRIMARY KEY,
        OperationId   INT           NOT NULL
                      CONSTRAINT FK_HeaderRules_Operations REFERENCES dbo.Operations(Id),
        HeaderName    NVARCHAR(100) NOT NULL,
        IsRequired    BIT           NOT NULL CONSTRAINT DF_HeaderRules_IsRequired DEFAULT (0),
        MaxLength     INT           NULL,
        RegexPattern  NVARCHAR(200) NULL,
        CONSTRAINT UQ_HeaderRules_Op_Name UNIQUE (OperationId, HeaderName)
    );
END
GO

/* ---------- 5. Configuration history (system-versioned temporal tables) ----------
   Every UPDATE/DELETE on a configuration table automatically keeps the previous
   row version in <Table>_History with its validity period (UTC).
   These tables contain only references/metadata - no secrets - so the history
   never contains key material or credentials.
   Example:  SELECT * FROM dbo.Operations FOR SYSTEM_TIME ALL WHERE Id = 1 ORDER BY ValidFrom;
   ------------------------------------------------------------------------------- */
DECLARE @tables TABLE (Name SYSNAME);
INSERT INTO @tables VALUES (N'Destinations'), (N'Operations'), (N'ParameterRules'), (N'HeaderRules'), (N'EncryptionProfiles');

DECLARE @t SYSNAME, @sql NVARCHAR(MAX);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @tables;
OPEN c;
FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECTPROPERTY(OBJECT_ID(N'dbo.' + @t), 'TableTemporalType') <> 2
    BEGIN
        IF COL_LENGTH(N'dbo.' + @t, N'ValidFrom') IS NULL
        BEGIN
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@t) + N' ADD
                ValidFrom DATETIME2(3) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL
                    CONSTRAINT ' + QUOTENAME(N'DF_' + @t + N'_ValidFrom') + N' DEFAULT (DATEADD(SECOND, -1, SYSUTCDATETIME())),
                ValidTo   DATETIME2(3) GENERATED ALWAYS AS ROW END HIDDEN NOT NULL
                    CONSTRAINT ' + QUOTENAME(N'DF_' + @t + N'_ValidTo') + N' DEFAULT (CONVERT(DATETIME2(3), ''9999-12-31 23:59:59.999'')),
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo);';
            EXEC sys.sp_executesql @sql;
        END

        SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@t) +
                   N' SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.' + QUOTENAME(@t + N'_History') + N'));';
        EXEC sys.sp_executesql @sql;
    END
    FETCH NEXT FROM c INTO @t;
END
CLOSE c;
DEALLOCATE c;
GO

/* ---------- 6. Seed data ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.EncryptionProfiles WHERE Name = N'customer-aes')
    INSERT INTO dbo.EncryptionProfiles (Name, Algorithm, KeyReference, KeyVersion)
    VALUES (N'customer-aes', 'AES-256-GCM', N'customer-aes', 1);
GO

-- Optional, validated header for customer.get
IF NOT EXISTS (SELECT 1 FROM dbo.HeaderRules hr JOIN dbo.Operations o ON o.Id = hr.OperationId
               WHERE o.OperationKey = N'customer.get' AND hr.HeaderName = N'X-Request-Source')
    INSERT INTO dbo.HeaderRules (OperationId, HeaderName, IsRequired, MaxLength, RegexPattern)
    SELECT Id, N'X-Request-Source', 0, 50, N'[A-Za-z0-9\-]+' FROM dbo.Operations WHERE OperationKey = N'customer.get';
GO

-- Encrypted variant of the same upstream call
IF NOT EXISTS (SELECT 1 FROM dbo.Operations WHERE OperationKey = N'customer.secure-get')
BEGIN
    INSERT INTO dbo.Operations (OperationKey, DestinationId, HttpMethod, PathTemplate, RequiresEncryption, EncryptionProfileId)
    SELECT N'customer.secure-get', d.Id, 'GET', N'/customers/{urlId}', 1, p.Id
    FROM dbo.Destinations d CROSS JOIN dbo.EncryptionProfiles p
    WHERE d.Name = N'CustomersApi' AND p.Name = N'customer-aes';

    INSERT INTO dbo.ParameterRules (OperationId, Name, IsRequired, DataType, MinValue, MaxValue, Location)
    SELECT Id, N'urlId', 1, 'int', 1, 2147483647, 'Path' FROM dbo.Operations WHERE OperationKey = N'customer.secure-get';
END
GO
