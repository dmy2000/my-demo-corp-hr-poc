<#
.SYNOPSIS
    Deploys or updates the My Demo Corp HR PoC application to IIS under Default Web Site (/hr)
    and configures a dedicated SQL Server user (hr_app) on HrPocDb.
#>
param(
    [string]$PublishSourceDir  = "$PSScriptRoot\..\publish",
    [string]$DataSourceDir     = "$PSScriptRoot\..\data",
    [string]$InstallPath       = "C:\inetpub\wwwroot\hr",
    [string]$AppPoolName       = "HrPocAppPool",
    [string]$SiteName          = "Default Web Site",
    [string]$AppSubPath        = "hr",
    [string]$SqlServerInstance = "localhost",
    [string]$DatabaseName      = "HrPocDb",
    [string]$AppSqlLogin       = "hr_app",
    [Parameter(Mandatory = $true)]
    [string]$AppSqlPassword,
    [string]$AdminSqlLogin     = "sa",
    [string]$AdminSqlPassword  = ""
)

$ErrorActionPreference = "Stop"

Write-Host "=== Deploying / Updating My Demo Corp HR PoC in IIS ($SiteName/$AppSubPath -> $InstallPath) ===" -ForegroundColor Cyan

Import-Module WebAdministration -ErrorAction Stop

# 1. Stop existing AppPool safely and wait for file handles to be released
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    $state = (Get-WebAppPoolState -Name $AppPoolName).Value
    if ($state -eq "Started") {
        Write-Host "Stopping AppPool '$AppPoolName'..."
        Stop-WebAppPool -Name $AppPoolName
        $timeout = 15
        $elapsed = 0
        while ($elapsed -lt $timeout) {
            if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq "Stopped") { break }
            Start-Sleep -Seconds 1
            $elapsed++
        }
        Start-Sleep -Seconds 3
    }
}

# Check and verify file locks are released
$lockedDll = Join-Path $InstallPath "HrPoc.Web.dll"
if (Test-Path $lockedDll) {
    Write-Host "Verifying file locks on '$lockedDll'..."
    $maxWait = 15
    $waitCount = 0
    while ($waitCount -lt $maxWait) {
        $stream = $null
        try {
            $stream = [System.IO.File]::Open($lockedDll, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            break
        } catch {
            Start-Sleep -Seconds 1
            $waitCount++
        } finally {
            if ($stream) {
                $stream.Close()
                $stream.Dispose()
            }
        }
    }
}

# 2. Provision SQL Server Database (HrPocDb) and dedicated SQL User (hr_app)
Write-Host "Provisioning database '$DatabaseName' and SQL login '$AppSqlLogin' on '$SqlServerInstance'..."
$masterConnStr = if ($AdminSqlPassword) {
    "Server=$SqlServerInstance;Database=master;User Id=$AdminSqlLogin;Password=$AdminSqlPassword;TrustServerCertificate=True"
} else {
    "Server=$SqlServerInstance;Database=master;Integrated Security=True;TrustServerCertificate=True"
}

$sqlCreateDbAndLogin = @"
IF DB_ID(N'$DatabaseName') IS NULL
BEGIN
    CREATE DATABASE [$DatabaseName];
END;

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'$AppSqlLogin')
BEGIN
    CREATE LOGIN [$AppSqlLogin] WITH PASSWORD = N'$AppSqlPassword', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$DatabaseName];
END
ELSE
BEGIN
    ALTER LOGIN [$AppSqlLogin] WITH PASSWORD = N'$AppSqlPassword', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [$DatabaseName];
    ALTER LOGIN [$AppSqlLogin] ENABLE;
END;
"@

$sqlGrantDbOwner = @"
USE [$DatabaseName];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$AppSqlLogin')
BEGIN
    CREATE USER [$AppSqlLogin] FOR LOGIN [$AppSqlLogin];
END
ELSE
BEGIN
    ALTER USER [$AppSqlLogin] WITH LOGIN = [$AppSqlLogin];
END;
ALTER ROLE [db_owner] ADD MEMBER [$AppSqlLogin];

IF OBJECT_ID(N'dbo.Locations', N'U') IS NULL
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
END;

IF OBJECT_ID(N'dbo.Departments', N'U') IS NULL
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
END;

IF OBJECT_ID(N'dbo.CostCenters', N'U') IS NULL
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
END;

IF OBJECT_ID(N'dbo.Employees', N'U') IS NULL
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
END;

IF OBJECT_ID(N'dbo.InstallationConfig', N'U') IS NULL
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
        [InstalledAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [UseOidcForWeb] BIT NOT NULL DEFAULT (0),
        [UseOidcForOData] BIT NOT NULL DEFAULT (0),
        [UseAuthorizationServer] BIT NOT NULL DEFAULT (0),
        [OidcAuthority] NVARCHAR(512) NULL,
        [OidcClientId] NVARCHAR(256) NULL,
        [OidcClientSecret] NVARCHAR(512) NULL,
        [OidcRedirectUri] NVARCHAR(512) NULL,
        [OidcScopes] NVARCHAR(512) NULL DEFAULT (N'openid profile email'),
        [OidcRequireHttpsMetadata] BIT NOT NULL DEFAULT (0),
        [AuthzServerIssuer] NVARCHAR(512) NULL,
        [AuthzAudience] NVARCHAR(512) NULL,
        [AuthzReadScope] NVARCHAR(256) NULL DEFAULT (N'hr:read'),
        [AuthzWriteScope] NVARCHAR(256) NULL DEFAULT (N'hr:write'),
        [AuthzRequiredClaimType] NVARCHAR(256) NULL,
        [AuthzRequiredClaimValue] NVARCHAR(256) NULL
    );
END;

IF COL_LENGTH(N'dbo.InstallationConfig', N'UseOidcForWeb') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [UseOidcForWeb] BIT NOT NULL DEFAULT (0);
IF COL_LENGTH(N'dbo.InstallationConfig', N'UseOidcForOData') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [UseOidcForOData] BIT NOT NULL DEFAULT (0);
IF COL_LENGTH(N'dbo.InstallationConfig', N'UseAuthorizationServer') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [UseAuthorizationServer] BIT NOT NULL DEFAULT (0);
IF COL_LENGTH(N'dbo.InstallationConfig', N'OidcAuthority') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [OidcAuthority] NVARCHAR(512) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'OidcClientId') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [OidcClientId] NVARCHAR(256) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'OidcClientSecret') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [OidcClientSecret] NVARCHAR(512) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'OidcRedirectUri') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [OidcRedirectUri] NVARCHAR(512) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'OidcScopes') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [OidcScopes] NVARCHAR(512) NULL DEFAULT (N'openid profile email');
IF COL_LENGTH(N'dbo.InstallationConfig', N'OidcRequireHttpsMetadata') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [OidcRequireHttpsMetadata] BIT NOT NULL DEFAULT (0);
IF COL_LENGTH(N'dbo.InstallationConfig', N'AuthzServerIssuer') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzServerIssuer] NVARCHAR(512) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'AuthzAudience') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzAudience] NVARCHAR(512) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'AuthzReadScope') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzReadScope] NVARCHAR(256) NULL DEFAULT (N'hr:read');
IF COL_LENGTH(N'dbo.InstallationConfig', N'AuthzWriteScope') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzWriteScope] NVARCHAR(256) NULL DEFAULT (N'hr:write');
IF COL_LENGTH(N'dbo.InstallationConfig', N'AuthzRequiredClaimType') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzRequiredClaimType] NVARCHAR(256) NULL;
IF COL_LENGTH(N'dbo.InstallationConfig', N'AuthzRequiredClaimValue') IS NULL
    ALTER TABLE [dbo].[InstallationConfig] ADD [AuthzRequiredClaimValue] NVARCHAR(256) NULL;

IF EXISTS (SELECT 1 FROM [dbo].[InstallationConfig])
BEGIN
    UPDATE [dbo].[InstallationConfig]
    SET [IISSiteName] = N'$SiteName',
        [IISAppSubPath] = N'$AppSubPath',
        [InstallPath] = N'$InstallPath',
        [AppPoolName] = N'$AppPoolName',
        [AppSqlLogin] = N'$AppSqlLogin',
        [AppUrl] = N'http://$($env:COMPUTERNAME.ToLowerInvariant())/$AppSubPath/',
        [Version] = N'1.3.0',
        [UpdatedAtUtc] = SYSUTCDATETIME();
END
ELSE
BEGIN
    INSERT INTO [dbo].[InstallationConfig]
        ([IISSiteName], [IISAppSubPath], [InstallPath], [AppPoolName], [AppSqlLogin], [AppUrl], [Version], [InstalledAtUtc], [UpdatedAtUtc])
    VALUES
        (N'$SiteName', N'$AppSubPath', N'$InstallPath', N'$AppPoolName', N'$AppSqlLogin', N'http://$($env:COMPUTERNAME.ToLowerInvariant())/$AppSubPath/', N'1.3.0', SYSUTCDATETIME(), SYSUTCDATETIME());
END;
"@

$conn = New-Object System.Data.SqlClient.SqlConnection($masterConnStr)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = $sqlCreateDbAndLogin
$cmd.ExecuteNonQuery() | Out-Null
$cmd.CommandText = $sqlGrantDbOwner
$cmd.ExecuteNonQuery() | Out-Null
$conn.Close()
Write-Host "Database '$DatabaseName', user '$AppSqlLogin', and InstallationConfig recorded." -ForegroundColor Green

# 3. Create install directories & copy files
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\data" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\logs" -Force | Out-Null

Write-Host "Copying application binaries and static assets..."
Copy-Item -Path "$PublishSourceDir\*" -Destination $InstallPath -Recurse -Force

# Ensure runtimeconfig.json allows rollForward to LatestMajor (.NET 8 / 9 / 10+)
$rtConfigPath = Join-Path $InstallPath "HrPoc.Web.runtimeconfig.json"
if (Test-Path $rtConfigPath) {
    try {
        $rtJson = Get-Content -Path $rtConfigPath -Raw | ConvertFrom-Json
        if ($rtJson.runtimeOptions -and $rtJson.runtimeOptions.rollForward -ne "LatestMajor") {
            $rtJson.runtimeOptions | Add-Member -MemberType NoteProperty -Name "rollForward" -Value "LatestMajor" -Force
            $utf8NoBom = New-Object System.Text.UTF8Encoding $false
            [System.IO.File]::WriteAllText($rtConfigPath, ($rtJson | ConvertTo-Json -Depth 10), $utf8NoBom)
            Write-Host "Configured 'rollForward: LatestMajor' in HrPoc.Web.runtimeconfig.json." -ForegroundColor Green
        }
    } catch {
        Write-Warning "Could not update HrPoc.Web.runtimeconfig.json: $($_.Exception.Message)"
    }
}

# CSV Preservation: Never overwrite existing CSV files in data/
$targetDataDir = Join-Path $InstallPath "data"
$existingCsvs = @(Get-ChildItem -Path $targetDataDir -Filter "*.csv" -File -ErrorAction SilentlyContinue)

if ($existingCsvs.Count -gt 0) {
    Write-Host "Preserved $($existingCsvs.Count) existing CSV data file(s) in '$targetDataDir' (custom user data not overwritten)." -ForegroundColor Green
} else {
    Write-Host "Copying CSV reference data..."
    Copy-Item -Path "$DataSourceDir\*.csv" -Destination $targetDataDir -Force
}

# 4. Write Production configuration using dedicated SQL user (hr_app)
$appConnStr = "Server=$SqlServerInstance;Database=$DatabaseName;User Id=$AppSqlLogin;Password=$AppSqlPassword;MultipleActiveResultSets=true;TrustServerCertificate=True"
$prodConfig = @{
    ConnectionStrings = @{
        DefaultConnection = $appConnStr
    }
    CsvDataDirectory = "./data"
} | ConvertTo-Json -Depth 5

Set-Content -Path "$InstallPath\appsettings.Production.json" -Value $prodConfig -Encoding UTF8

# 5. Configure IIS AppPool (No Managed Code for ASP.NET Core Module V2)
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    Write-Host "Creating IIS AppPool '$AppPoolName'..."
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""

# 6. Configure IIS WebApplication (/hr under Default Web Site)
$existingApp = Get-WebApplication -Site $SiteName -Name $AppSubPath -ErrorAction SilentlyContinue
if (-not $existingApp) {
    Write-Host "Creating IIS WebApplication '/$AppSubPath' under '$SiteName'..."
    New-WebApplication -Site $SiteName -Name $AppSubPath -PhysicalPath $InstallPath -ApplicationPool $AppPoolName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName\$AppSubPath" -Name "physicalPath" -Value $InstallPath
    Set-ItemProperty "IIS:\Sites\$SiteName\$AppSubPath" -Name "applicationPool" -Value $AppPoolName
}

Start-WebAppPool -Name $AppPoolName
Start-Website -Name $SiteName

Write-Host "=== Deployment / Update Complete! Application is running at http://localhost/$AppSubPath/ ===" -ForegroundColor Green
