<#
.SYNOPSIS
    Interactive & Automated Installer for My Demo Corp -- HR System PoC (v1.1)
    Target Platform: Windows Server / Windows 10/11 + IIS + Microsoft SQL Server

.DESCRIPTION
    1. Checks prerequisites (Administrator rights, IIS Web-Server + Management Tools,
       ASP.NET Core 8.0 Hosting Bundle / AspNetCoreModuleV2, and application binaries)
       and offers to install or build any missing components automatically.
    2. Prompts for IIS and SQL Server connection parameters:
       - SQL Server host & optional instance name
       - Admin credentials for DB creation:
         * Current Windows User
         * Windows Account in UPN (user@domain.com) or DOMAIN\user format
         * SQL Server Login (e.g., sa)
       - Dedicated SQL user for the HR PoC application (e.g., hr_app)
    3. Validates SQL Server connectivity, permissions, and authentication mode.
    4. Provisions the database & user, deploys the application to IIS, and runs a smoke test.

.EXAMPLE
    .\deploy\Install-HrPoc.ps1
#>
[CmdletBinding()]
param(
    [string]$SqlHost = "",
    [string]$SqlInstance = "",
    [ValidateSet("", "CurrentWindows", "WindowsUPN", "SqlLogin")]
    [string]$AdminAuthType = "",
    [string]$AdminUsername = "",
    [string]$AdminPassword = "",
    [string]$DatabaseName = "",
    [string]$AppSqlLogin = "",
    [string]$AppSqlPassword = "",
    [string]$IISSiteName = "",
    [string]$IISAppSubPath = "",
    [string]$InstallPath = "",
    [string]$AppPoolName = "HrPocAppPool",
    [switch]$Unattended
)

$ErrorActionPreference = "Stop"

# ============================================================================
# Helper Functions & UI Formatting
# ============================================================================
function Write-Step([string]$Title) {
    Write-Host ""
    Write-Host "============================================================================" -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host "============================================================================" -ForegroundColor Cyan
}

function Write-Ok([string]$Msg) {
    Write-Host "  [OK] $Msg" -ForegroundColor Green
}

function Write-Warn([string]$Msg) {
    Write-Host "  [!]  $Msg" -ForegroundColor Yellow
}

function Write-Err([string]$Msg) {
    Write-Host "  [X]  $Msg" -ForegroundColor Red
}

function Read-Default([string]$Prompt, [string]$DefaultValue) {
    if ($Unattended) { return $DefaultValue }
    $inputVal = Read-Host "  $Prompt [$DefaultValue]"
    if ([string]::IsNullOrWhiteSpace($inputVal)) { return $DefaultValue }
    return $inputVal.Trim()
}

function Read-YesNo([string]$Prompt, [bool]$DefaultYes = $true) {
    if ($Unattended) { return $DefaultYes }
    $suffix = if ($DefaultYes) { "[Y/n]" } else { "[y/N]" }
    while ($true) {
        $ans = Read-Host "  $Prompt $suffix"
        if ([string]::IsNullOrWhiteSpace($ans)) { return $DefaultYes }
        if ($ans -match "^(y|yes)$") { return $true }
        if ($ans -match "^(n|no)$") { return $false }
        Write-Host "  Please enter Y or N." -ForegroundColor Yellow
    }
}

function Read-SecretPlain([string]$Prompt, [string]$DefaultSecret = "") {
    if ($Unattended) { return $DefaultSecret }
    while ($true) {
        $hint = if ($DefaultSecret) { " [Enter to keep current value]" } else { "" }
        $sec = Read-Host "  $Prompt$hint" -AsSecureString
        if ($sec.Length -eq 0) {
            if ($DefaultSecret) { return $DefaultSecret }
            Write-Host "  Password cannot be empty. Please enter a password." -ForegroundColor Yellow
            continue
        }
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
        try {
            return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        } finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

# Win32 Impersonation Helper for Windows UPN (user@domain.tld) or DOMAIN\user authentication
$impersonatorCode = @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class Win32Impersonator
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername,
        string lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern bool CloseHandle(IntPtr handle);

    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    private const int LOGON32_PROVIDER_DEFAULT = 0;
    private const int LOGON32_PROVIDER_WINNT50 = 3;

    public static IntPtr Logon(string username, string password, bool networkOnly)
    {
        string user = username;
        string domain = null;

        if (username.IndexOf('\\') >= 0)
        {
            var parts = username.Split(new char[] { '\\' }, 2);
            domain = parts[0];
            user = parts[1];
        }
        else if (username.IndexOf((char)64) < 0)
        {
            domain = ".";
        }

        IntPtr token = IntPtr.Zero;
        int logonType = networkOnly ? LOGON32_LOGON_NEW_CREDENTIALS : LOGON32_LOGON_INTERACTIVE;
        int provider = networkOnly ? LOGON32_PROVIDER_WINNT50 : LOGON32_PROVIDER_DEFAULT;

        bool ok = LogonUser(user, domain, password, logonType, provider, out token);
        if (!ok && !networkOnly)
        {
            ok = LogonUser(user, domain, password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out token);
        }

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(string.Format("Windows LogonUser failed for '{0}' (Win32 Error Code: {1}).", username, err));
        }
        return token;
    }
}
'@

if (-not ("Win32Impersonator" -as [type])) {
    Add-Type -TypeDefinition $impersonatorCode -Language CSharp
}

function Invoke-SqlQuery {
    param(
        [string]$ServerInstance,
        [string]$Database = "master",
        [string]$AuthType, # CurrentWindows | WindowsUPN | SqlLogin
        [string]$Username = "",
        [string]$Password = "",
        [string]$Query,
        [switch]$NonQuery
    )

    Add-Type -AssemblyName "System.Data"
    $token = [IntPtr]::Zero
    $impersonationContext = $null

    try {
        if ($AuthType -eq "SqlLogin") {
            $connStr = "Server=$ServerInstance;Database=$Database;User Id=$Username;Password=$Password;TrustServerCertificate=True;Connect Timeout=10"
        } else {
            $connStr = "Server=$ServerInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10"
            if ($AuthType -eq "WindowsUPN") {
                $isLocal = ($ServerInstance -match "^(localhost|127\.0\.0\.1|\.|" + [Regex]::Escape($env:COMPUTERNAME) + ")(\\|$)")
                $token = [Win32Impersonator]::Logon($Username, $Password, (-not $isLocal))
                $identity = New-Object System.Security.Principal.WindowsIdentity($token)
                $impersonationContext = $identity.Impersonate()
            }
        }

        $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
        $conn.Open()
        try {
            $cmd = $conn.CreateCommand()
            $cmd.CommandTimeout = 30
            $cmd.CommandText = $Query
            if ($NonQuery) {
                return $cmd.ExecuteNonQuery()
            } else {
                $reader = $cmd.ExecuteReader()
                try {
                    $results = New-Object System.Collections.Generic.List[PSObject]
                    while ($reader.Read()) {
                        $obj = [ordered]@{}
                        for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                            $colName = $reader.GetName($i)
                            if ([string]::IsNullOrEmpty($colName)) { $colName = "Column$i" }
                            $val = $reader.GetValue($i)
                            if ($val -is [System.DBNull]) { $val = $null }
                            $obj[$colName] = $val
                        }
                        $results.Add([PSCustomObject]$obj)
                    }
                    return ,$results.ToArray()
                } finally {
                    $reader.Close()
                }
            }
        } finally {
            $conn.Close()
        }
    } finally {
        if ($impersonationContext) {
            $impersonationContext.Undo()
            $impersonationContext.Dispose()
        }
        if ($token -ne [IntPtr]::Zero) {
            [void][Win32Impersonator]::CloseHandle($token)
        }
    }
}

# ============================================================================
# Step 0: Check Administrator Privileges & Locate Project Paths
# ============================================================================
Clear-Host
Write-Step "My Demo Corp -- HR System PoC (v1.1) Installer"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Err "This installer must be run as Administrator (to configure IIS and Windows features)."
    if (Read-YesNo "Restart this script automatically as Administrator?" $true) {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        exit
    }
    exit 1
}
Write-Ok "Running with Administrator privileges."

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish"
$dataDir    = Join-Path $repoRoot "data"
$csprojPath = Join-Path $repoRoot "src\HrPoc.Web\HrPoc.Web.csproj"

# ============================================================================
# Step 1: Check Prerequisites (IIS, AspNetCoreModuleV2, Build Artifacts)
# ============================================================================
Write-Step "Step 1: Checking Prerequisites"

# 1.1 Check IIS (W3SVC & WebAdministration)
$w3svc = Get-Service -Name W3SVC -ErrorAction SilentlyContinue
$webAdminAvailable = [bool](Get-Module -ListAvailable -Name WebAdministration)

if (-not $w3svc -or -not $webAdminAvailable) {
    Write-Warn "IIS (Web-Server) or IIS Management Scripting Tools are not installed."
    if (Read-YesNo "Install IIS (Web-Server + Management Tools) now?" $true) {
        if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
            Write-Host "  Installing IIS via Install-WindowsFeature..." -ForegroundColor Cyan
            Install-WindowsFeature -Name Web-Server, Web-Common-Http, Web-Default-Doc, Web-Static-Content, Web-Http-Errors, Web-Http-Logging, Web-Performance, Web-Security, Web-Filtering, Web-Mgmt-Console, Web-Scripting-Tools -IncludeManagementTools | Out-Null
        } else {
            Write-Host "  Installing IIS via Enable-WindowsOptionalFeature..." -ForegroundColor Cyan
            Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-CommonHttpFeatures, IIS-DefaultDocument, IIS-StaticContent, IIS-ManagementConsole, IIS-ManagementScriptingTools -All -NoRestart | Out-Null
        }
        $w3svc = Get-Service -Name W3SVC -ErrorAction SilentlyContinue
        $webAdminAvailable = [bool](Get-Module -ListAvailable -Name WebAdministration)
    } else {
        Write-Err "Cannot proceed without IIS installed."
        exit 1
    }
}

if ($w3svc.Status -ne "Running") {
    Start-Service W3SVC
}
Import-Module WebAdministration -ErrorAction Stop
Write-Ok "IIS Service (W3SVC) is running and WebAdministration module is loaded."

# 1.2 Check ASP.NET Core Module V2 (AspNetCoreModuleV2)
$ancmDll = "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
$ancmRegistered = [bool](Get-WebGlobalModule -Name "AspNetCoreModuleV2" -ErrorAction SilentlyContinue)

if (-not (Test-Path $ancmDll) -or -not $ancmRegistered) {
    Write-Warn "IIS ASP.NET Core Module V2 (AspNetCoreModuleV2) is not registered in IIS."

    $cachedBundle = Get-ChildItem "C:\ProgramData\Package Cache\*\dotnet-hosting-*-win.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cachedBundle) {
        Write-Host "  Found cached Hosting Bundle installer: $($cachedBundle.FullName)" -ForegroundColor Cyan
        if (Read-YesNo "Run Repair on the installed ASP.NET Core Hosting Bundle to register AspNetCoreModuleV2 in IIS?" $true) {
            Write-Host "  Repairing ASP.NET Core Hosting Bundle..." -ForegroundColor Cyan
            $p = Start-Process -FilePath $cachedBundle.FullName -ArgumentList "/repair /quiet /norestart" -Wait -PassThru
            & net stop was /y 2>$null | Out-Null
            & net start w3svc 2>$null | Out-Null
        }
    } else {
        if (Read-YesNo "Download and install the official Microsoft ASP.NET Core 8.0 Hosting Bundle now?" $true) {
            $bundleInstaller = "$env:TEMP\dotnet-hosting-win.exe"
            Write-Host "  Downloading https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe ..." -ForegroundColor Cyan
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri "https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe" -OutFile $bundleInstaller -UseBasicParsing
            Write-Host "  Installing ASP.NET Core Hosting Bundle (silent)..." -ForegroundColor Cyan
            Start-Process -FilePath $bundleInstaller -ArgumentList "/install /quiet /norestart" -Wait | Out-Null
            & net stop was /y 2>$null | Out-Null
            & net start w3svc 2>$null | Out-Null
        }
    }

    $ancmRegistered = [bool](Get-WebGlobalModule -Name "AspNetCoreModuleV2" -ErrorAction SilentlyContinue)
    if (-not (Test-Path $ancmDll) -and -not $ancmRegistered) {
        Write-Err "AspNetCoreModuleV2 is still not detected. Please install the ASP.NET Core Hosting Bundle and re-run."
        exit 1
    }
}
Write-Ok "ASP.NET Core Module V2 (AspNetCoreModuleV2) is installed and registered in IIS."

# 1.3 Check Published Binaries & CSV Data
if (-not (Test-Path (Join-Path $publishDir "HrPoc.Web.dll"))) {
    Write-Warn "Compiled publish folder ('$publishDir') not found."

    $dotnetExe = $null
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCmd) {
        $sdks = & $dotnetCmd.Source --list-sdks 2>$null
        if ($sdks) { $dotnetExe = $dotnetCmd.Source }
    }
    if (-not $dotnetExe -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) {
        $sdks = & "$env:USERPROFILE\.dotnet\dotnet.exe" --list-sdks 2>$null
        if ($sdks) { $dotnetExe = "$env:USERPROFILE\.dotnet\dotnet.exe" }
    }

    if (-not $dotnetExe -and (Test-Path $csprojPath)) {
        Write-Warn ".NET 8 SDK is not installed on this machine to compile the source code."
        if (Read-YesNo "Download a portable .NET 8 SDK to '$env:USERPROFILE\.dotnet' to build the project now?" $true) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $installScript = "$env:TEMP\dotnet-install.ps1"
            Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript -UseBasicParsing
            & powershell -NoProfile -ExecutionPolicy Bypass -File $installScript -Channel 8.0 -InstallDir "$env:USERPROFILE\.dotnet"
            if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
                $dotnetExe = "$env:USERPROFILE\.dotnet\dotnet.exe"
            }
        }
    }

    if ($dotnetExe -and (Test-Path $csprojPath)) {
        if (Read-YesNo "Build and publish HrPoc.Web (compact framework-dependent release) now?" $true) {
            Write-Host "  Building HrPoc.Web..." -ForegroundColor Cyan
            & $dotnetExe publish $csprojPath -c Release -o $publishDir
        }
    }

    if (-not (Test-Path (Join-Path $publishDir "HrPoc.Web.dll"))) {
        Write-Err "Publish binaries not found at '$publishDir'. Run 'dotnet publish src/HrPoc.Web/HrPoc.Web.csproj -c Release -o ./publish' or copy the 'publish' folder."
        exit 1
    }
}
Write-Ok "Application binaries verified at '$publishDir'."

if (-not (Test-Path (Join-Path $dataDir "MDC-HR-Data.csv"))) {
    Write-Err "CSV template data files (MDC-*.csv) not found at '$dataDir'."
    exit 1
}
Write-Ok "CSV reference files verified at '$dataDir'."

# ============================================================================
# Step 2: Collect SQL Server & IIS Configuration
# ============================================================================
Write-Step "Step 2: Database & IIS Configuration"

while ($true) {
    $SqlHost     = Read-Default "SQL Server Hostname or IP" ($(if ($SqlHost) { $SqlHost } else { "localhost" }))
    $SqlInstance = Read-Default "SQL Server Instance Name (leave blank for default MSSQLSERVER)" $SqlInstance

    $fullSqlServer = if ([string]::IsNullOrWhiteSpace($SqlInstance) -or $SqlInstance -ieq "MSSQLSERVER") {
        $SqlHost
    } else {
        "$SqlHost\$SqlInstance"
    }

    Write-Host ""
    Write-Host "  Select Administrator Authentication Method (to create DB & login on '$fullSqlServer'):" -ForegroundColor Cyan
    Write-Host "    [1] Windows Account in UPN (user@domain.com) or DOMAIN\user format"
    Write-Host "    [2] SQL Server Admin Login (e.g. 'sa')"
    Write-Host "    [3] Current Windows Session ($(whoami))"

    if ([string]::IsNullOrWhiteSpace($AdminAuthType)) {
        $choice = Read-Default "Select option (1, 2, or 3)" "2"
        switch ($choice) {
            "1" { $AdminAuthType = "WindowsUPN" }
            "2" { $AdminAuthType = "SqlLogin" }
            "3" { $AdminAuthType = "CurrentWindows" }
            default { $AdminAuthType = "SqlLogin" }
        }
    }

    if ($AdminAuthType -eq "WindowsUPN") {
        $defaultUpn = if ($AdminUsername) { $AdminUsername } elseif ($env:USERDNSDOMAIN) { "administrator@$($env:USERDNSDOMAIN.ToLowerInvariant())" } else { "administrator@corp.example.com" }
        $AdminUsername = Read-Default "Windows Admin Username (UPN e.g. admin@corp.example.com or DOMAIN\user)" $defaultUpn
        $AdminPassword = Read-SecretPlain "Windows Admin Password for '$AdminUsername'" $AdminPassword
    } elseif ($AdminAuthType -eq "SqlLogin") {
        $defaultSa = if ($AdminUsername -and $AdminUsername -notmatch "\\") { $AdminUsername } else { "sa" }
        $AdminUsername = Read-Default "SQL Admin Login" $defaultSa
        $AdminPassword = Read-SecretPlain "SQL Admin Password for '$AdminUsername'" $AdminPassword
    } else {
        $AdminUsername = $(whoami)
        $AdminPassword = ""
    }

    Write-Host ""
    $DatabaseName   = Read-Default "Target Database Name to create/use" ($(if ($DatabaseName) { $DatabaseName } else { "HrPocDb" }))
    $AppSqlLogin    = Read-Default "Dedicated SQL Login for HR PoC application" ($(if ($AppSqlLogin) { $AppSqlLogin } else { "hr_app" }))
    $AppSqlPassword = Read-SecretPlain "Password for dedicated SQL Login '$AppSqlLogin'" $AppSqlPassword

    # ========================================================================
    # Step 3: Validate SQL Server Connectivity & Permissions
    # ========================================================================
    Write-Step "Step 3: Validating SQL Server Connection ('$fullSqlServer')"
    try {
        $checkQuery = @"
SELECT
    @@SERVERNAME AS ServerName,
    CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)) AS ProductVersion,
    CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128)) AS Edition,
    CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS INT) AS WindowsAuthOnly,
    SUSER_SNAME() AS ConnectedLogin,
    IS_SRVROLEMEMBER('sysadmin') AS IsSysAdmin,
    IS_SRVROLEMEMBER('dbcreator') AS IsDbCreator,
    IS_SRVROLEMEMBER('securityadmin') AS IsSecurityAdmin;
"@
        $rows = @(Invoke-SqlQuery -ServerInstance $fullSqlServer -Database "master" -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $checkQuery)
        $row = $rows[0]

        Write-Ok "Connected to SQL Server: $($row.ServerName) ($($row.Edition), v$($row.ProductVersion))"
        Write-Ok "Authenticated as: $($row.ConnectedLogin)"

        if ($row.IsSysAdmin -ne 1 -and ($row.IsDbCreator -ne 1 -or $row.IsSecurityAdmin -ne 1)) {
            Write-Err "Account '$($row.ConnectedLogin)' does not have 'sysadmin' (or 'dbcreator' + 'securityadmin') rights required to create databases and logins."
            if ($Unattended) { exit 1 }
            $AdminAuthType = ""
            continue
        }
        Write-Ok "Admin permissions verified (sysadmin=$($row.IsSysAdmin))."

        if ($row.WindowsAuthOnly -eq 1) {
            Write-Warn "SQL Server is currently in 'Windows Authentication Only' mode, which blocks SQL login '$AppSqlLogin'."
            $isLocalSql = ($SqlHost -match "^(localhost|127\.0\.0\.1|\.|" + [Regex]::Escape($env:COMPUTERNAME) + ")$")
            if ($isLocalSql -and (Read-YesNo "Enable Mixed Mode Authentication (SQL + Windows) and restart SQL Server now?" $true)) {
                $enableMixedSql = "EXEC xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'LoginMode', REG_DWORD, 2;"
                [void](Invoke-SqlQuery -ServerInstance $fullSqlServer -Database "master" -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $enableMixedSql -NonQuery)
                $sqlSvcName = if ([string]::IsNullOrWhiteSpace($SqlInstance) -or $SqlInstance -ieq "MSSQLSERVER") { "MSSQLSERVER" } else { "MSSQL`$$SqlInstance" }
                Write-Host "  Restarting service '$sqlSvcName'..." -ForegroundColor Cyan
                Restart-Service -Name $sqlSvcName -Force
                Start-Sleep -Seconds 3
                Write-Ok "Mixed Mode Authentication enabled on '$fullSqlServer'."
            } else {
                Write-Err "Mixed Mode Authentication is required for SQL user '$AppSqlLogin'."
                if ($Unattended) { exit 1 }
                $AdminAuthType = ""
                continue
            }
        } else {
            Write-Ok "SQL Server Mixed Mode Authentication is enabled."
        }
        break
    } catch {
        Write-Err "SQL Server connection test failed: $($_.Exception.Message)"
        if ($Unattended) { exit 1 }
        if (-not (Read-YesNo "Re-enter SQL Server connection parameters?" $true)) {
            exit 1
        }
        $AdminAuthType = ""
    }
}

# Prompt for IIS Site & Virtual Path
Write-Host ""
$IISSiteName   = Read-Default "IIS Website Name" ($(if ($IISSiteName) { $IISSiteName } else { "Default Web Site" }))
$IISAppSubPath = Read-Default "IIS Virtual Application Subpath (e.g. 'hr' for http://server/hr/)" ($(if ($IISAppSubPath) { $IISAppSubPath } else { "hr" }))
$IISAppSubPath = $IISAppSubPath.Trim('/')
$defaultInstallDir = if ($InstallPath) { $InstallPath } else { "C:\inetpub\wwwroot\$IISAppSubPath" }
$InstallPath   = Read-Default "Physical Installation Directory" $defaultInstallDir
$AppPoolName   = Read-Default "IIS Application Pool Name" $AppPoolName

Write-Host ""
Write-Host "  Deployment Summary:" -ForegroundColor Cyan
Write-Host "    SQL Server         : $fullSqlServer"
Write-Host "    Admin Auth         : $AdminAuthType ($AdminUsername)"
Write-Host "    Target Database    : $DatabaseName"
Write-Host "    App SQL User       : $AppSqlLogin (db_owner on $DatabaseName)"
Write-Host "    IIS Target         : $IISSiteName / /$IISAppSubPath"
Write-Host "    Physical Directory : $InstallPath"
Write-Host "    AppPool            : $AppPoolName"
Write-Host ""

if (-not (Read-YesNo "Proceed with deployment?" $true)) {
    Write-Warn "Deployment cancelled by user."
    exit 0
}

# ============================================================================
# Step 4: Provision Database, Deploy to IIS & Verify
# ============================================================================
Write-Step "Step 4: Provisioning SQL Server & Deploying to IIS"

# 4.1 Create Database & Login using Admin Credentials
$sqlProvisionMaster = @"
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

$sqlProvisionDb = @"
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

[void](Invoke-SqlQuery -ServerInstance $fullSqlServer -Database "master" -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $sqlProvisionMaster -NonQuery)
[void](Invoke-SqlQuery -ServerInstance $fullSqlServer -Database $DatabaseName -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $sqlProvisionDb -NonQuery)
Write-Ok "Database '$DatabaseName' and dedicated user '$AppSqlLogin' (db_owner) provisioned."

# 4.2 Verify application SQL login works
$testAppRows = @(Invoke-SqlQuery -ServerInstance $fullSqlServer -Database $DatabaseName -AuthType "SqlLogin" -Username $AppSqlLogin -Password $AppSqlPassword -Query "SELECT SUSER_SNAME() AS LoginName, DB_NAME() AS DbName;")
$testAppRow = $testAppRows[0]
Write-Ok "Verified login as '$($testAppRow.LoginName)' on database '$($testAppRow.DbName)'."

# 4.3 Stop IIS AppPool if running
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq "Started") {
        Write-Host "  Stopping IIS AppPool '$AppPoolName'..."
        Stop-WebAppPool -Name $AppPoolName
        Start-Sleep -Seconds 2
    }
}

# 4.4 Copy Files & Write Production Config
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\data" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\logs" -Force | Out-Null

Write-Host "  Copying application files to '$InstallPath'..."
Copy-Item -Path "$publishDir\*" -Destination $InstallPath -Recurse -Force
Copy-Item -Path "$dataDir\*.csv" -Destination "$InstallPath\data" -Force

$appConnStr = "Server=$fullSqlServer;Database=$DatabaseName;User Id=$AppSqlLogin;Password=$AppSqlPassword;MultipleActiveResultSets=true;TrustServerCertificate=True"
$prodConfig = @{
    ConnectionStrings = @{
        DefaultConnection = $appConnStr
    }
    CsvDataDirectory = "./data"
} | ConvertTo-Json -Depth 5
Set-Content -Path "$InstallPath\appsettings.Production.json" -Value $prodConfig -Encoding UTF8
Write-Ok "Application files and 'appsettings.Production.json' written."

# 4.5 Configure IIS AppPool & Web Application
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""

$existingApp = Get-WebApplication -Site $IISSiteName -Name $IISAppSubPath -ErrorAction SilentlyContinue
if (-not $existingApp) {
    New-WebApplication -Site $IISSiteName -Name $IISAppSubPath -PhysicalPath $InstallPath -ApplicationPool $AppPoolName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$IISSiteName\$IISAppSubPath" -Name "physicalPath" -Value $InstallPath
    Set-ItemProperty "IIS:\Sites\$IISSiteName\$IISAppSubPath" -Name "applicationPool" -Value $AppPoolName
}

Start-WebAppPool -Name $AppPoolName
Start-Website -Name $IISSiteName
Write-Ok "IIS AppPool '$AppPoolName' and application '/$IISAppSubPath' started."

# 4.6 Run Post-Deployment Smoke Test
Write-Step "Step 5: Verifying Live Application & Initial CSV Seeding"
$baseUrl = "http://localhost/$IISAppSubPath"
try {
    Write-Host "  Waiting for initial database seeding ($baseUrl/api/dashboard/stats)..." -ForegroundColor Cyan
    $stats = Invoke-RestMethod -Uri "$baseUrl/api/dashboard/stats" -TimeoutSec 45
    Write-Ok "Web UI & REST API responded! Database Provider: $($stats.databaseProvider)"
    Write-Ok "Seeded Counts: Employees=$($stats.totalEmployees), Departments=$($stats.totalDepartments), CostCenters=$($stats.totalCostCenters), Locations=$($stats.totalLocations)"

    $meta = Invoke-WebRequest -Uri "$baseUrl/odata/`$metadata" -UseBasicParsing -TimeoutSec 15
    Write-Ok "OData v4 Endpoint verified ($baseUrl/odata/`$metadata -> HTTP $($meta.StatusCode))"
} catch {
    Write-Warn "Smoke test warning: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Green
Write-Host "  INSTALLATION COMPLETE!" -ForegroundColor Green
Write-Host "  Web UI URL        : http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/" -ForegroundColor White
Write-Host "  OData v4 Root URL : http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/odata" -ForegroundColor White
Write-Host "  OData Metadata    : http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/odata/`$metadata" -ForegroundColor White
Write-Host "============================================================================" -ForegroundColor Green
