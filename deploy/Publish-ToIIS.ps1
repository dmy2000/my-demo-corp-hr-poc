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
        [UpdatedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

IF EXISTS (SELECT 1 FROM [dbo].[InstallationConfig])
BEGIN
    UPDATE [dbo].[InstallationConfig]
    SET [IISSiteName] = N'$SiteName',
        [IISAppSubPath] = N'$AppSubPath',
        [InstallPath] = N'$InstallPath',
        [AppPoolName] = N'$AppPoolName',
        [AppSqlLogin] = N'$AppSqlLogin',
        [AppUrl] = N'http://$($env:COMPUTERNAME.ToLowerInvariant())/$AppSubPath/',
        [Version] = N'1.1.2',
        [UpdatedAtUtc] = SYSUTCDATETIME();
END
ELSE
BEGIN
    INSERT INTO [dbo].[InstallationConfig]
        ([IISSiteName], [IISAppSubPath], [InstallPath], [AppPoolName], [AppSqlLogin], [AppUrl], [Version], [InstalledAtUtc], [UpdatedAtUtc])
    VALUES
        (N'$SiteName', N'$AppSubPath', N'$InstallPath', N'$AppPoolName', N'$AppSqlLogin', N'http://$($env:COMPUTERNAME.ToLowerInvariant())/$AppSubPath/', N'1.1.2', SYSUTCDATETIME(), SYSUTCDATETIME());
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
