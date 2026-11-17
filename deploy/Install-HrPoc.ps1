<#
.SYNOPSIS
    Interactive & Automated Installer / Updater for My Demo Corp -- HR System PoC (v1.3)
    Target Platform: Windows Server / Windows 10/11 + IIS + Microsoft SQL Server

.DESCRIPTION
    1. Checks prerequisites (Administrator rights, IIS Web-Server + Management Tools,
       ASP.NET Core 8.0 Hosting Bundle / AspNetCoreModuleV2, and application binaries)
       and offers to install or build any missing components automatically.
    2. Collects SQL Server connection info (host, instance, admin authentication method,
       and target database name).
    3. Checks if the target database already exists and contains the InstallationConfig table:
       - If YES: switches directly to UPDATE MODE. Displays existing configuration (IIS site,
         virtual path, physical directory, app pool, SQL user) and prompts to proceed with the
         update without asking unnecessary questions.
       - If NO: prompts for the remaining parameters (dedicated SQL user, IIS website, virtual
         subpath, physical path, app pool) for a fresh installation.
    4. Safely stops the IIS Application Pool and verifies all file locks (e.g. w3wp.exe) are released.
    5. Deploys updated application binaries while preserving any existing CSV data files in the
       destination 'data' folder (never overwriting user data during updates).
    6. Saves/updates installation settings in dbo.InstallationConfig and runs post-deploy verification.

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
    [string]$AppPoolName = "",
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

function Stop-WebAppPoolSafely {
    param(
        [string]$PoolName,
        [string]$TargetFolder = ""
    )

    if (-not (Test-Path "IIS:\AppPools\$PoolName")) { return }

    $state = (Get-WebAppPoolState -Name $PoolName).Value
    if ($state -eq "Started") {
        Write-Host "  Stopping IIS Application Pool '$PoolName'..." -ForegroundColor Cyan
        Stop-WebAppPool -Name $PoolName
    }

    # Wait for AppPool to report Stopped state (up to 15 seconds)
    $timeout = 15
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        $state = (Get-WebAppPoolState -Name $PoolName).Value
        if ($state -eq "Stopped") { break }
        Start-Sleep -Seconds 1
        $elapsed++
    }

    # Safety delay for worker processes to shut down
    Start-Sleep -Seconds 3

    # Check and wait until file locks are released on critical binaries
    if ($TargetFolder -and (Test-Path $TargetFolder)) {
        $testFile = Join-Path $TargetFolder "HrPoc.Web.dll"
        if (Test-Path $testFile) {
            Write-Host "  Checking that worker process released file handles..." -ForegroundColor Cyan
            $maxWait = 15
            $waitCount = 0
            $released = $false
            while ($waitCount -lt $maxWait) {
                $stream = $null
                try {
                    $stream = [System.IO.File]::Open($testFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                    $released = $true
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
            if ($released) {
                Write-Ok "Application Pool stopped and file handles released."
            } else {
                Write-Warn "File '$testFile' is still locked after $maxWait seconds. Proceeding with deployment..."
            }
        }
    } else {
        Write-Ok "Application Pool '$PoolName' stopped."
    }
}

# ============================================================================
# Step 0: Check Administrator Privileges & Locate Project Paths
# ============================================================================
Clear-Host
Write-Step "My Demo Corp -- HR System PoC (v1.3) Installer / Updater"

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

# 1.2 Check ASP.NET Core Module V2 (AspNetCoreModuleV2) & Shared Runtime (>= 8.0)
function Get-AspNetCoreSharedRuntimes {
    $found = New-Object System.Collections.Generic.List[string]
    $sharedDirs = @(
        "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App",
        "$env:ProgramW6432\dotnet\shared\Microsoft.AspNetCore.App"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    foreach ($dir in $sharedDirs) {
        Get-ChildItem -Path $dir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $verObj = $null
            $verStr = $_.Name -split '-' | Select-Object -First 1
            if ([Version]::TryParse($verStr, [ref]$verObj) -and $verObj.Major -ge 8) {
                if (-not $found.Contains($_.Name)) { $found.Add($_.Name) }
            }
        }
    }
    return ,$found.ToArray()
}

$ancmDll = "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
$ancmRegistered = [bool](Get-WebGlobalModule -Name "AspNetCoreModuleV2" -ErrorAction SilentlyContinue)
$aspNetRuntimes = @(Get-AspNetCoreSharedRuntimes)

if (-not (Test-Path $ancmDll) -or -not $ancmRegistered -or $aspNetRuntimes.Count -eq 0) {
    if (-not (Test-Path $ancmDll) -or -not $ancmRegistered) {
        Write-Warn "IIS ASP.NET Core Module V2 (AspNetCoreModuleV2) is not registered in IIS."
    }
    if ($aspNetRuntimes.Count -eq 0) {
        Write-Warn "No Microsoft.AspNetCore.App shared runtime (>= 8.0) found in Program Files\dotnet\shared\Microsoft.AspNetCore.App."
    }

    $cachedBundle = Get-ChildItem "C:\ProgramData\Package Cache\*\dotnet-hosting-*-win.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cachedBundle -and $aspNetRuntimes.Count -gt 0) {
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
    $aspNetRuntimes = @(Get-AspNetCoreSharedRuntimes)
    if ((-not (Test-Path $ancmDll) -and -not $ancmRegistered) -or $aspNetRuntimes.Count -eq 0) {
        Write-Err "AspNetCoreModuleV2 or Microsoft.AspNetCore.App (>= 8.0) runtime is still not detected. Please install the ASP.NET Core 8.0+ Hosting Bundle and re-run."
        exit 1
    }
}
$runtimeSummary = ($aspNetRuntimes | Sort-Object) -join ", "
Write-Ok "ASP.NET Core Module V2 & Microsoft.AspNetCore.App runtime ($runtimeSummary) verified."

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

# ============================================================================
# Step 2: Database Connection & Mode Detection
# ============================================================================
Write-Step "Step 2: Database Connection & Mode Detection"

$fullSqlServer = ""
$IsUpdate = $false
$existingConfig = $null

while ($true) {
    $SqlHost     = Read-Default "SQL Server Hostname or IP" ($(if ($SqlHost) { $SqlHost } else { "localhost" }))
    $SqlInstance = Read-Default "SQL Server Instance Name (leave blank for default MSSQLSERVER)" $SqlInstance

    $fullSqlServer = if ([string]::IsNullOrWhiteSpace($SqlInstance) -or $SqlInstance -ieq "MSSQLSERVER") {
        $SqlHost
    } else {
        "$SqlHost\$SqlInstance"
    }

    Write-Host ""
    Write-Host "  Select Administrator Authentication Method (to connect to '$fullSqlServer'):" -ForegroundColor Cyan
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
    $DatabaseName = Read-Default "Target Database Name" ($(if ($DatabaseName) { $DatabaseName } else { "HrPocDb" }))

    # ========================================================================
    # Step 3: Validate SQL Server & Inspect Existing Database / Config
    # ========================================================================
    Write-Step "Step 3: Validating SQL Server Connection & Inspecting '$DatabaseName'"
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
    IS_SRVROLEMEMBER('securityadmin') AS IsSecurityAdmin,
    DB_ID(N'$DatabaseName') AS TargetDbId;
"@
        $rows = @(Invoke-SqlQuery -ServerInstance $fullSqlServer -Database "master" -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $checkQuery)
        $row = $rows[0]

        Write-Ok "Connected to SQL Server: $($row.ServerName) ($($row.Edition), v$($row.ProductVersion))"
        Write-Ok "Authenticated as: $($row.ConnectedLogin)"

        if ($row.IsSysAdmin -ne 1 -and ($row.IsDbCreator -ne 1 -or $row.IsSecurityAdmin -ne 1)) {
            Write-Err "Account '$($row.ConnectedLogin)' does not have 'sysadmin' (or 'dbcreator' + 'securityadmin') rights required to manage databases and logins."
            if ($Unattended) { exit 1 }
            $AdminAuthType = ""
            continue
        }
        Write-Ok "Admin permissions verified (sysadmin=$($row.IsSysAdmin))."

        if ($row.WindowsAuthOnly -eq 1) {
            Write-Warn "SQL Server is currently in 'Windows Authentication Only' mode, which blocks SQL login."
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
                Write-Err "Mixed Mode Authentication is required for application SQL user."
                if ($Unattended) { exit 1 }
                $AdminAuthType = ""
                continue
            }
        } else {
            Write-Ok "SQL Server Mixed Mode Authentication is enabled."
        }

        # Check if database exists and whether InstallationConfig table is present
        if ($row.TargetDbId) {
            Write-Ok "Target database '$DatabaseName' found on '$fullSqlServer'."
            $cfgQuery = @"
IF OBJECT_ID(N'dbo.InstallationConfig', N'U') IS NOT NULL
BEGIN
    SELECT TOP 1
        Id,
        IISSiteName,
        IISAppSubPath,
        InstallPath,
        AppPoolName,
        AppSqlLogin,
        ISNULL(AppUrl, '') AS AppUrl,
        ISNULL(Version, '') AS Version,
        CONVERT(VARCHAR(30), InstalledAtUtc, 126) AS InstalledAtUtc,
        CONVERT(VARCHAR(30), UpdatedAtUtc, 126) AS UpdatedAtUtc
    FROM [dbo].[InstallationConfig]
    ORDER BY Id DESC;
END
ELSE
BEGIN
    SELECT NULL AS IISSiteName WHERE 1 = 0;
END;
"@
            $cfgRows = @(Invoke-SqlQuery -ServerInstance $fullSqlServer -Database $DatabaseName -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $cfgQuery)
            if ($cfgRows.Count -gt 0 -and $cfgRows[0].IISSiteName) {
                $existingConfig = $cfgRows[0]
                $IsUpdate = $true
            }
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

# ============================================================================
# Mode Selection: UPDATE MODE vs FRESH INSTALL MODE
# ============================================================================
if ($IsUpdate) {
    # UPDATE MODE: Load existing settings without asking redundant questions
    $IISSiteName   = $existingConfig.IISSiteName
    $IISAppSubPath = $existingConfig.IISAppSubPath
    $InstallPath   = $existingConfig.InstallPath
    $AppPoolName   = $existingConfig.AppPoolName
    $AppSqlLogin   = $existingConfig.AppSqlLogin
    $installedVer  = if ($existingConfig.Version) { $existingConfig.Version } else { "1.1" }
    $appUrl        = if ($existingConfig.AppUrl) { $existingConfig.AppUrl } else { "http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/" }

    # Attempt to extract current SQL password from appsettings.Production.json
    $prodConfigPath = Join-Path $InstallPath "appsettings.Production.json"
    if (Test-Path $prodConfigPath) {
        try {
            $rawJson = Get-Content $prodConfigPath -Raw | ConvertFrom-Json
            $existingConnStr = $rawJson.ConnectionStrings.DefaultConnection
            if ($existingConnStr -match "(?i)Password=([^;]+)") {
                $AppSqlPassword = $matches[1]
            }
        } catch { }
    }

    if (-not $AppSqlPassword) {
        $AppSqlPassword = Read-SecretPlain "Password for existing dedicated SQL Login '$AppSqlLogin'" ""
    }

    Write-Host ""
    Write-Host "============================================================================" -ForegroundColor Green
    Write-Host "  EXISTING INSTALLATION DETECTED -> UPDATE MODE" -ForegroundColor Green
    Write-Host "============================================================================" -ForegroundColor Green
    Write-Host "    Target Database       : $DatabaseName"
    Write-Host "    Installed Version     : $installedVer"
    Write-Host "    IIS Website Name      : $IISSiteName"
    Write-Host "    Virtual Application   : /$IISAppSubPath"
    Write-Host "    Physical Directory    : $InstallPath"
    Write-Host "    Application Pool      : $AppPoolName"
    Write-Host "    Dedicated SQL Login   : $AppSqlLogin"
    Write-Host "    Application URL       : $appUrl"
    Write-Host ""
    Write-Host "    New Version to Deploy : v1.3.0" -ForegroundColor Cyan
    Write-Host "============================================================================" -ForegroundColor Green
    Write-Host ""

    if (-not (Read-YesNo "Proceed with updating this installation to v1.3.0?" $true)) {
        Write-Warn "Update cancelled by user."
        exit 0
    }
} else {
    # FRESH INSTALL MODE: Collect remaining parameters
    Write-Host ""
    Write-Host "  New installation configuration for database '$DatabaseName':" -ForegroundColor Cyan
    $AppSqlLogin    = Read-Default "Dedicated SQL Login for HR PoC application" ($(if ($AppSqlLogin) { $AppSqlLogin } else { "hr_app" }))
    $AppSqlPassword = Read-SecretPlain "Password for dedicated SQL Login '$AppSqlLogin'" $AppSqlPassword

    $IISSiteName    = Read-Default "IIS Website Name" ($(if ($IISSiteName) { $IISSiteName } else { "Default Web Site" }))
    $IISAppSubPath  = Read-Default "IIS Virtual Application Subpath (e.g. 'hr' for http://server/hr/)" ($(if ($IISAppSubPath) { $IISAppSubPath } else { "hr" }))
    $IISAppSubPath  = $IISAppSubPath.Trim('/')
    $defaultInstall = if ($InstallPath) { $InstallPath } else { "C:\inetpub\wwwroot\$IISAppSubPath" }
    $InstallPath    = Read-Default "Physical Installation Directory" $defaultInstall
    $AppPoolName    = if ($AppPoolName) { $AppPoolName } else { "HrPocAppPool" }
    $AppPoolName    = Read-Default "IIS Application Pool Name" $AppPoolName

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
}

# ============================================================================
# Step 4: Provision SQL Server, Stop IIS & Deploy Binaries
# ============================================================================
Write-Step "Step 4: Provisioning SQL Server & Deploying to IIS"

# 4.1 Create Database & Login if needed using Admin Credentials
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
Write-Ok "Database '$DatabaseName' and dedicated user '$AppSqlLogin' (db_owner) verified."

# 4.2 Ensure core entity tables and record/update dbo.InstallationConfig in target database
$defaultAppUrl = "http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/"
$sqlRecordConfig = @"
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
    SET [IISSiteName] = N'$IISSiteName',
        [IISAppSubPath] = N'$IISAppSubPath',
        [InstallPath] = N'$InstallPath',
        [AppPoolName] = N'$AppPoolName',
        [AppSqlLogin] = N'$AppSqlLogin',
        [AppUrl] = N'$defaultAppUrl',
        [Version] = N'1.3.0',
        [UpdatedAtUtc] = SYSUTCDATETIME();
END
ELSE
BEGIN
    INSERT INTO [dbo].[InstallationConfig]
        ([IISSiteName], [IISAppSubPath], [InstallPath], [AppPoolName], [AppSqlLogin], [AppUrl], [Version], [InstalledAtUtc], [UpdatedAtUtc])
    VALUES
        (N'$IISSiteName', N'$IISAppSubPath', N'$InstallPath', N'$AppPoolName', N'$AppSqlLogin', N'$defaultAppUrl', N'1.3.0', SYSUTCDATETIME(), SYSUTCDATETIME());
END;
"@

[void](Invoke-SqlQuery -ServerInstance $fullSqlServer -Database $DatabaseName -AuthType $AdminAuthType -Username $AdminUsername -Password $AdminPassword -Query $sqlRecordConfig -NonQuery)
Write-Ok "Installation configuration recorded in '$DatabaseName.dbo.InstallationConfig'."

# 4.3 Stop IIS AppPool and wait for file locks to be released
Stop-WebAppPoolSafely -PoolName $AppPoolName -TargetFolder $InstallPath

# 4.4 Copy Application Binaries & Preserve CSV Data Files
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\data" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallPath\logs" -Force | Out-Null

Write-Host "  Copying updated application binaries to '$InstallPath'..."
Copy-Item -Path "$publishDir\*" -Destination $InstallPath -Recurse -Force

# Ensure runtimeconfig.json allows rollForward to LatestMajor (.NET 8 / 9 / 10+)
$rtConfigPath = Join-Path $InstallPath "HrPoc.Web.runtimeconfig.json"
if (Test-Path $rtConfigPath) {
    try {
        $rtJson = Get-Content -Path $rtConfigPath -Raw | ConvertFrom-Json
        if ($rtJson.runtimeOptions -and $rtJson.runtimeOptions.rollForward -ne "LatestMajor") {
            $rtJson.runtimeOptions | Add-Member -MemberType NoteProperty -Name "rollForward" -Value "LatestMajor" -Force
            $utf8NoBom = New-Object System.Text.UTF8Encoding $false
            [System.IO.File]::WriteAllText($rtConfigPath, ($rtJson | ConvertTo-Json -Depth 10), $utf8NoBom)
            Write-Ok "Configured 'rollForward: LatestMajor' in HrPoc.Web.runtimeconfig.json."
        }
    } catch {
        Write-Warn "Could not update HrPoc.Web.runtimeconfig.json: $($_.Exception.Message)"
    }
}

# CSV Preservation: Never overwrite existing CSV files in data/ if already present
$targetDataDir = Join-Path $InstallPath "data"
$existingCsvs = @(Get-ChildItem -Path $targetDataDir -Filter "*.csv" -File -ErrorAction SilentlyContinue)

if ($existingCsvs.Count -gt 0) {
    Write-Ok "Preserved $($existingCsvs.Count) existing CSV data file(s) in '$targetDataDir' (user data was not overwritten)."
} else {
    Write-Host "  Copying template CSV reference data to '$targetDataDir'..."
    if (Test-Path (Join-Path $dataDir "MDC-HR-Data.csv")) {
        Copy-Item -Path "$dataDir\*.csv" -Destination $targetDataDir -Force
        Write-Ok "Template CSV data files copied to '$targetDataDir'."
    }
}

# 4.5 Write appsettings.Production.json
$appConnStr = "Server=$fullSqlServer;Database=$DatabaseName;User Id=$AppSqlLogin;Password=$AppSqlPassword;MultipleActiveResultSets=true;TrustServerCertificate=True"
$prodConfig = @{
    ConnectionStrings = @{
        DefaultConnection = $appConnStr
    }
    CsvDataDirectory = "./data"
} | ConvertTo-Json -Depth 5
Set-Content -Path "$InstallPath\appsettings.Production.json" -Value $prodConfig -Encoding UTF8
Write-Ok "'appsettings.Production.json' configured with dedicated SQL login '$AppSqlLogin'."

# 4.6 Configure IIS AppPool & Web Application
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
Write-Ok "IIS Application Pool '$AppPoolName' and application '/$IISAppSubPath' started."

# ============================================================================
# Step 5: Verification Smoke Test
# ============================================================================
Write-Step "Step 5: Verifying Application Health"
$localTestUrl = "http://localhost/$IISAppSubPath"
try {
    Write-Host "  Querying $localTestUrl/api/dashboard/stats..." -ForegroundColor Cyan
    $stats = Invoke-RestMethod -Uri "$localTestUrl/api/dashboard/stats" -TimeoutSec 45
    Write-Ok "Application responded! Version: $($stats.installedVersion), Database Provider: $($stats.databaseProvider)"
    Write-Ok "Live Counts: Employees=$($stats.totalEmployees), Departments=$($stats.totalDepartments), CostCenters=$($stats.totalCostCenters), Locations=$($stats.totalLocations)"

    $meta = Invoke-WebRequest -Uri "$localTestUrl/odata/`$metadata" -UseBasicParsing -TimeoutSec 15
    Write-Ok "OData v4 Metadata verified ($localTestUrl/odata/`$metadata -> HTTP $($meta.StatusCode))"
} catch {
    Write-Warn "Smoke test warning: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Green
if ($IsUpdate) {
    Write-Host "  UPDATE COMPLETE! (v1.3.0)" -ForegroundColor Green
} else {
    Write-Host "  INSTALLATION COMPLETE! (v1.3.0)" -ForegroundColor Green
}
Write-Host "  Web UI URL        : http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/" -ForegroundColor White
Write-Host "  OData v4 Root URL : http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/odata" -ForegroundColor White
Write-Host "  OData Metadata    : http://$($env:COMPUTERNAME.ToLowerInvariant())/$IISAppSubPath/odata/`$metadata" -ForegroundColor White
Write-Host "============================================================================" -ForegroundColor Green
