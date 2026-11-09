<#
.SYNOPSIS
    Deploys the My Demo Corp HR PoC application to IIS under Default Web Site (/hr)
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

Write-Host "=== Deploying My Demo Corp HR PoC to IIS ($SiteName/$AppSubPath -> $InstallPath) ===" -ForegroundColor Cyan

Import-Module WebAdministration -ErrorAction Stop

# 1. Stop existing AppPool if running
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    $state = (Get-WebAppPoolState -Name $AppPoolName).Value
    if ($state -eq "Started") {
        Write-Host "Stopping AppPool '$AppPoolName'..."
        Stop-WebAppPool -Name $AppPoolName
        Start-Sleep -Seconds 2
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
"@

$conn = New-Object System.Data.SqlClient.SqlConnection($masterConnStr)
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = $sqlCreateDbAndLogin
$cmd.ExecuteNonQuery() | Out-Null
$cmd.CommandText = $sqlGrantDbOwner
$cmd.ExecuteNonQuery() | Out-Null
$conn.Close()
Write-Host "Database '$DatabaseName' and SQL user '$AppSqlLogin' (db_owner) configured." -ForegroundColor Green

# 3. Create install directories & copy files
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\data" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\logs" -Force | Out-Null

Write-Host "Copying application binaries and static assets..."
Copy-Item -Path "$PublishSourceDir\*" -Destination $InstallPath -Recurse -Force
Write-Host "Copying CSV reference data..."
Copy-Item -Path "$DataSourceDir\*.csv" -Destination "$InstallPath\data" -Force

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

Write-Host "=== Deployment Complete! Application is running at http://localhost/$AppSubPath/ ===" -ForegroundColor Green
