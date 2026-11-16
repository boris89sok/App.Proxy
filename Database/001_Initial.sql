-- יצירת מסד הנתונים (דלגי אם הוא כבר קיים)
CREATE DATABASE SecureProxyDb;
GO
USE SecureProxyDb;
GO

-- 1. שרתי יעד
CREATE TABLE dbo.Destinations (
    Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Destinations PRIMARY KEY,
    Name            NVARCHAR(100)  NOT NULL CONSTRAINT UQ_Destinations_Name UNIQUE,
    BaseUrl         NVARCHAR(500)  NOT NULL,
    IsEnabled       BIT            NOT NULL CONSTRAINT DF_Destinations_IsEnabled DEFAULT (1),
    TimeoutSeconds  INT            NOT NULL CONSTRAINT DF_Destinations_Timeout DEFAULT (10)
                    CONSTRAINT CK_Destinations_Timeout CHECK (TimeoutSeconds BETWEEN 1 AND 120),
    AllowedPorts    NVARCHAR(100)  NULL,          -- למשל '443,5001'
    CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_Destinations_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_Destinations_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    RowVersion      ROWVERSION     NOT NULL
);
GO

-- 2. פעולות מותרות
CREATE TABLE dbo.Operations (
    Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Operations PRIMARY KEY,
    OperationKey            NVARCHAR(100) NOT NULL CONSTRAINT UQ_Operations_Key UNIQUE,  -- 'customer.get'
    DestinationId           INT           NOT NULL
                            CONSTRAINT FK_Operations_Destinations REFERENCES dbo.Destinations(Id),
    HttpMethod              VARCHAR(10)   NOT NULL CONSTRAINT DF_Operations_Method DEFAULT ('GET')
                            CONSTRAINT CK_Operations_Method CHECK (HttpMethod IN ('GET')),  -- שלב 1: רק GET
    PathTemplate            NVARCHAR(300) NOT NULL,     -- '/customers/{urlId}'
    IsEnabled               BIT           NOT NULL CONSTRAINT DF_Operations_IsEnabled DEFAULT (1),
    MaxRequestBytes         INT           NOT NULL CONSTRAINT DF_Operations_MaxReq DEFAULT (65536),
    AllowUnknownParameters  BIT           NOT NULL CONSTRAINT DF_Operations_AllowUnknown DEFAULT (0),
    RequiresEncryption      BIT           NOT NULL CONSTRAINT DF_Operations_ReqEnc DEFAULT (0),
    CreatedAt               DATETIME2(3)  NOT NULL CONSTRAINT DF_Operations_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt               DATETIME2(3)  NOT NULL CONSTRAINT DF_Operations_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    RowVersion              ROWVERSION    NOT NULL
);
CREATE INDEX IX_Operations_DestinationId ON dbo.Operations(DestinationId);
GO

-- 3. חוקי פרמטרים
CREATE TABLE dbo.ParameterRules (
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ParameterRules PRIMARY KEY,
    OperationId  INT           NOT NULL
                 CONSTRAINT FK_ParameterRules_Operations REFERENCES dbo.Operations(Id) ON DELETE CASCADE,
    Name         NVARCHAR(100) NOT NULL,
    IsRequired   BIT           NOT NULL CONSTRAINT DF_ParameterRules_IsRequired DEFAULT (0),
    DataType     VARCHAR(20)   NOT NULL
                 CONSTRAINT CK_ParameterRules_DataType CHECK (DataType IN ('string','int','guid','bool','date')),
    MaxLength    INT           NULL,
    MinValue     BIGINT        NULL,
    MaxValue     BIGINT        NULL,
    RegexPattern NVARCHAR(200) NULL,
    Location     VARCHAR(10)   NOT NULL
                 CONSTRAINT CK_ParameterRules_Location CHECK (Location IN ('Path','Query')),
    CONSTRAINT UQ_ParameterRules_Op_Name UNIQUE (OperationId, Name)
);
GO

-- 4. לוג בקשות (בלי גוף הבקשה, בלי כותרות ובלי סודות)
CREATE TABLE dbo.RequestLogs (
    Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RequestLogs PRIMARY KEY,
    CorrelationId   NVARCHAR(64)  NOT NULL,
    OperationKey    NVARCHAR(100) NULL,           -- NULL אם הבקשה נדחתה לפני שזוהתה פעולה
    DestinationId   INT           NULL,
    StartedAt       DATETIME2(3)  NOT NULL,
    CompletedAt     DATETIME2(3)  NULL,
    DurationMs      INT           NULL,
    Forwarded       BIT           NOT NULL CONSTRAINT DF_RequestLogs_Forwarded DEFAULT (0),
    UpstreamStatus  INT           NULL,
    ProxyStatus     INT           NOT NULL,
    Outcome         VARCHAR(30)   NOT NULL,       -- Success / Rejected / UpstreamError / Timeout / ClientCancelled / Failed
    Reason          NVARCHAR(300) NULL
);
CREATE INDEX IX_RequestLogs_CorrelationId ON dbo.RequestLogs(CorrelationId);
CREATE INDEX IX_RequestLogs_StartedAt     ON dbo.RequestLogs(StartedAt);
GO

-- נתוני דוגמה
INSERT INTO dbo.Destinations (Name, BaseUrl, TimeoutSeconds, AllowedPorts)
VALUES (N'CustomersApi', N'https://localhost:5001', 10, N'5001');

INSERT INTO dbo.Operations (OperationKey, DestinationId, HttpMethod, PathTemplate)
VALUES (N'customer.get', SCOPE_IDENTITY(), 'GET', N'/customers/{urlId}');

INSERT INTO dbo.ParameterRules (OperationId, Name, IsRequired, DataType, MinValue, MaxValue, Location)
VALUES (SCOPE_IDENTITY(), N'urlId', 1, 'int', 1, 2147483647, 'Path');
GO
