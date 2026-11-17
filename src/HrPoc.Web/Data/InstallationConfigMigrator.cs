using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Data;

public static class InstallationConfigMigrator
{
    private static readonly SemaphoreSlim _migrationLock = new(1, 1);

    private const string MigrationSql = @"
IF OBJECT_ID(N'[dbo].[Locations]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Locations] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Locations] PRIMARY KEY,
        [Name] NVARCHAR(300) NOT NULL,
        [StructuredName] NVARCHAR(400) NOT NULL,
        [Description] NVARCHAR(500) NULL,
        [CountryShortcut] NVARCHAR(16) NULL,
        [ZipCode] NVARCHAR(32) NULL,
        [City] NVARCHAR(150) NULL,
        [Street] NVARCHAR(250) NULL,
        [Phone] NVARCHAR(64) NULL,
        [Fax] NVARCHAR(64) NULL,
        [ManagerUsername] NVARCHAR(64) NULL,
        [AssistantManagerUsername] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Locations_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Locations_UpdatedAt] DEFAULT (SYSUTCDATETIME())
    );

    CREATE UNIQUE INDEX [IX_Locations_StructuredName] ON [dbo].[Locations]([StructuredName]);
    CREATE INDEX [IX_Locations_CountryShortcut] ON [dbo].[Locations]([CountryShortcut]);
    CREATE INDEX [IX_Locations_City] ON [dbo].[Locations]([City]);
END

IF OBJECT_ID(N'[dbo].[Departments]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Departments] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Departments] PRIMARY KEY,
        [Name] NVARCHAR(200) NOT NULL,
        [ShortName] NVARCHAR(100) NULL,
        [StructuredName] NVARCHAR(400) NOT NULL,
        [ParentStructuredName] NVARCHAR(400) NULL,
        [ParentDepartmentId] INT NULL,
        [FunctionalShortcut] NVARCHAR(64) NULL,
        [TreeLevel] INT NOT NULL CONSTRAINT [DF_Departments_TreeLevel] DEFAULT (1),
        [ManagerUsername] NVARCHAR(64) NULL,
        [DeputyManagerUsername] NVARCHAR(64) NULL,
        [AdContainerForNewHires] NVARCHAR(500) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Departments_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Departments_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [FK_Departments_Parent] FOREIGN KEY ([ParentDepartmentId]) REFERENCES [dbo].[Departments]([Id])
    );

    CREATE UNIQUE INDEX [IX_Departments_StructuredName] ON [dbo].[Departments]([StructuredName]);
    CREATE INDEX [IX_Departments_ShortName] ON [dbo].[Departments]([ShortName]);
    CREATE INDEX [IX_Departments_ParentDepartmentId] ON [dbo].[Departments]([ParentDepartmentId]);
END

IF OBJECT_ID(N'[dbo].[CostCenters]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CostCenters] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CostCenters] PRIMARY KEY,
        [Code] NVARCHAR(64) NOT NULL,
        [StructuredName] NVARCHAR(200) NOT NULL,
        [ShortName] NVARCHAR(200) NULL,
        [ParentStructuredName] NVARCHAR(200) NULL,
        [ParentCostCenterId] INT NULL,
        [CompanyMainStructure] NVARCHAR(64) NULL,
        [CompanyCountryStructure] NVARCHAR(64) NULL,
        [TreeLevel] INT NOT NULL CONSTRAINT [DF_CostCenters_TreeLevel] DEFAULT (1),
        [ManagerUsername] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_CostCenters_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_CostCenters_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [FK_CostCenters_Parent] FOREIGN KEY ([ParentCostCenterId]) REFERENCES [dbo].[CostCenters]([Id])
    );

    CREATE INDEX [IX_CostCenters_StructuredName] ON [dbo].[CostCenters]([StructuredName]);
    CREATE INDEX [IX_CostCenters_Code] ON [dbo].[CostCenters]([Code]);
    CREATE INDEX [IX_CostCenters_ParentCostCenterId] ON [dbo].[CostCenters]([ParentCostCenterId]);
END

IF OBJECT_ID(N'[dbo].[Employees]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Employees] (
        [Id] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Employees] PRIMARY KEY,
        [Username] NVARCHAR(64) NOT NULL,
        [PersonnelNumber] NVARCHAR(32) NOT NULL,
        [FirstName] NVARCHAR(100) NOT NULL,
        [LastName] NVARCHAR(100) NOT NULL,
        [Initials] NVARCHAR(16) NULL,
        [Email] NVARCHAR(200) NULL,
        [JobTitle] NVARCHAR(150) NULL,
        [EmploymentStatus] NVARCHAR(32) NOT NULL CONSTRAINT [DF_Employees_Status] DEFAULT (N'Active'),
        [HireDate] DATE NOT NULL,
        [TerminationDate] DATE NULL,
        [Phone] NVARCHAR(64) NULL,
        [Fax] NVARCHAR(64) NULL,
        [Street] NVARCHAR(250) NULL,
        [City] NVARCHAR(150) NULL,
        [ZipCode] NVARCHAR(32) NULL,
        [DepartmentId] INT NULL,
        [CostCenterId] INT NULL,
        [LocationId] INT NULL,
        [DepartmentStructuredName] NVARCHAR(400) NULL,
        [CostCenterStructuredName] NVARCHAR(200) NULL,
        [LocationStructuredName] NVARCHAR(400) NULL,
        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Employees_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_Employees_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [FK_Employees_Departments] FOREIGN KEY ([DepartmentId]) REFERENCES [dbo].[Departments]([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Employees_CostCenters] FOREIGN KEY ([CostCenterId]) REFERENCES [dbo].[CostCenters]([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Employees_Locations] FOREIGN KEY ([LocationId]) REFERENCES [dbo].[Locations]([Id]) ON DELETE SET NULL
    );

    CREATE UNIQUE INDEX [IX_Employees_Username] ON [dbo].[Employees]([Username]);
    CREATE UNIQUE INDEX [IX_Employees_PersonnelNumber] ON [dbo].[Employees]([PersonnelNumber]);
    CREATE INDEX [IX_Employees_LastName] ON [dbo].[Employees]([LastName]);
    CREATE INDEX [IX_Employees_EmploymentStatus] ON [dbo].[Employees]([EmploymentStatus]);
    CREATE INDEX [IX_Employees_DepartmentId] ON [dbo].[Employees]([DepartmentId]);
    CREATE INDEX [IX_Employees_CostCenterId] ON [dbo].[Employees]([CostCenterId]);
    CREATE INDEX [IX_Employees_LocationId] ON [dbo].[Employees]([LocationId]);
    CREATE INDEX [IX_Employees_UpdatedAt] ON [dbo].[Employees]([UpdatedAt]);
END

IF OBJECT_ID(N'[dbo].[InstallationConfig]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[InstallationConfig] (
        [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [IISSiteName] NVARCHAR(256) NOT NULL,
        [IISAppSubPath] NVARCHAR(256) NOT NULL,
        [InstallPath] NVARCHAR(512) NOT NULL,
        [AppPoolName] NVARCHAR(256) NOT NULL,
        [AppSqlLogin] NVARCHAR(128) NOT NULL,
        [AppUrl] NVARCHAR(512) NULL,
        [Version] NVARCHAR(64) NOT NULL,
        [InstalledAtUtc] DATETIME2 NOT NULL,
        [UpdatedAtUtc] DATETIME2 NOT NULL,
        [UseOidcForWeb] BIT NOT NULL CONSTRAINT DF_InstallationConfig_UseOidcForWeb DEFAULT 0,
        [UseOidcForOData] BIT NOT NULL CONSTRAINT DF_InstallationConfig_UseOidcForOData DEFAULT 0,
        [UseAuthorizationServer] BIT NOT NULL CONSTRAINT DF_InstallationConfig_UseAuthorizationServer DEFAULT 0,
        [OidcAuthority] NVARCHAR(512) NULL,
        [OidcClientId] NVARCHAR(256) NULL,
        [OidcClientSecret] NVARCHAR(512) NULL,
        [OidcRedirectUri] NVARCHAR(512) NULL,
        [OidcScopes] NVARCHAR(512) NULL,
        [OidcRequireHttpsMetadata] BIT NOT NULL CONSTRAINT DF_InstallationConfig_OidcRequireHttpsMetadata DEFAULT 0,
        [AuthzServerIssuer] NVARCHAR(512) NULL,
        [AuthzAudience] NVARCHAR(512) NULL,
        [AuthzReadScope] NVARCHAR(256) NULL,
        [AuthzWriteScope] NVARCHAR(256) NULL,
        [AuthzRequiredClaimType] NVARCHAR(256) NULL,
        [AuthzRequiredClaimValue] NVARCHAR(256) NULL
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'UseOidcForWeb') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [UseOidcForWeb] BIT NOT NULL CONSTRAINT DF_InstallationConfig_UseOidcForWeb DEFAULT 0;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'UseOidcForOData') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [UseOidcForOData] BIT NOT NULL CONSTRAINT DF_InstallationConfig_UseOidcForOData DEFAULT 0;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'UseAuthorizationServer') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [UseAuthorizationServer] BIT NOT NULL CONSTRAINT DF_InstallationConfig_UseAuthorizationServer DEFAULT 0;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'OidcAuthority') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [OidcAuthority] NVARCHAR(512) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'OidcClientId') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [OidcClientId] NVARCHAR(256) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'OidcClientSecret') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [OidcClientSecret] NVARCHAR(512) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'OidcRedirectUri') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [OidcRedirectUri] NVARCHAR(512) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'OidcScopes') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [OidcScopes] NVARCHAR(512) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'OidcRequireHttpsMetadata') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [OidcRequireHttpsMetadata] BIT NOT NULL CONSTRAINT DF_InstallationConfig_OidcRequireHttpsMetadata DEFAULT 0;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'AuthzServerIssuer') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzServerIssuer] NVARCHAR(512) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'AuthzAudience') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzAudience] NVARCHAR(512) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'AuthzReadScope') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzReadScope] NVARCHAR(256) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'AuthzWriteScope') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzWriteScope] NVARCHAR(256) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'AuthzRequiredClaimType') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzRequiredClaimType] NVARCHAR(256) NULL;

    IF COL_LENGTH(N'[dbo].[InstallationConfig]', N'AuthzRequiredClaimValue') IS NULL
        ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzRequiredClaimValue] NVARCHAR(256) NULL;
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[InstallationConfig])
BEGIN
    INSERT INTO [dbo].[InstallationConfig] (
        [IISSiteName], [IISAppSubPath], [InstallPath], [AppPoolName], [AppSqlLogin],
        [AppUrl], [Version], [InstalledAtUtc], [UpdatedAtUtc],
        [UseOidcForWeb], [UseOidcForOData], [UseAuthorizationServer],
        [OidcScopes], [AuthzReadScope], [AuthzWriteScope]
    ) VALUES (
        'Default Web Site', 'hr', 'C:\inetpub\wwwroot\hr', 'HrPocAppPool', 'hr_app',
        'http://localhost/hr', '1.3.0', SYSUTCDATETIME(), SYSUTCDATETIME(),
        0, 0, 0,
        'openid profile email', 'hr:read', 'hr:write'
    );
END
";

    public static async Task EnsureSchemaAndDefaultRowAsync(HrDbContext db, CancellationToken ct = default)
    {
        await _migrationLock.WaitAsync(ct);
        try
        {
            await db.Database.EnsureCreatedAsync(ct);
            await db.Database.ExecuteSqlRawAsync(MigrationSql, ct);
        }
        finally
        {
            _migrationLock.Release();
        }
    }
}
