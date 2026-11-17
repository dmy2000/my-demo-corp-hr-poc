# My Demo Corp — HR System (PoC)

An authoritative **Human Resources (HR) Data Source** web application and **Full CRUD OData v4 / REST API** designed for Identity & Access Management (IAM / IGA / IDM) lab environments, Proof-of-Concept (PoC) deployments, and connector integration testing.

---

## Overview & Purpose

When building or demonstrating Identity Management (IDM) solutions, administrators and integrators need a realistic, controllable **Authoritative HR Source** to test identity lifecycle workflows (**Joiner / Mover / Leaver**) without relying on production ERP/HRIS platforms.

**My Demo Corp — HR System (PoC)** provides:
- **Interactive Web UI**: A clean, fast, zero-CDN Single-Page Application (SPA) that works seamlessly in internet-isolated lab networks. Create new hires, transfer employees across departments/cost centers/locations, change employment statuses (`Active`, `OnLeave`, `Terminated`, `Suspended`), manage organizational hierarchies, and track recent modifications.
- **OpenID Connect (OIDC) SSO & Web UI Gate**: Native browser-based Single Sign-On (SSO) supporting any standard OIDC 1.0 Identity Provider. Includes an intuitive **SSO Gate** overlay with "Sign in with SSO" and a dedicated "Settings" link to guarantee admin lockout prevention if OIDC parameters change.
- **Full CRUD OData v4 API (`/odata`)**: Standard OData v4 endpoints with CSDL XML schema (`/odata/$metadata`) supporting `GET` (`$filter`, `$select`, `$expand`, `$orderby`, `$top`, `$skip`, `$count`), `GET(key)`, `POST`, `PATCH` (`Delta<T>`), `PUT`, and `DELETE` across all four core entities (`Employees`, `Departments`, `CostCenters`, `Locations`).
- **OIDC / OAuth 2.0 Bearer Authentication for OData v4**: Protects headless API requests and IDM connectors (`/odata/*`) with JWT Bearer tokens validated against provider discovery documents (`/.well-known/openid-configuration`) and live JWKS signing keys.
- **OAuth 2.0 API Authorization Server & Scopes**: Granular access control supporting Authorization Server resource/audience validation (`resource` / `aud`), standard scopes (`hr:read` for queries, `hr:write` for mutations), and custom claim verification. Configurable policy toggle between "Enforce Authorization Server" vs "Allow any authenticated user".
- **Dedicated Settings UI & Diagnostic Tools (`/hr/settings.html`)**: Real-time configuration management stored directly in `dbo.InstallationConfig`, with live OIDC metadata discovery testing, server-side client_credentials token tests, and JWT token inspection.
- **Bidirectional Organizational Resolution**: When creating or updating an employee via OData, you can supply either numeric foreign keys (`DepartmentId`, `CostCenterId`, `LocationId`) **or** hierarchical path strings (`DepartmentStructuredName`, `CostCenterStructuredName`, `LocationStructuredName`) — the backend automatically resolves and synchronizes both fields.
- **Customizable CSV Baseline & One-Click Reset**: Automatically seeds the SQL Server database on first startup from semicolon-delimited (`;`) CSV templates in the [`data/`](data/) folder, and allows resetting the database back to the CSV baseline at any time from the Web UI or API.

---

## Technology Stack & Architecture

- **Backend**: ASP.NET Core 8.0 (`net8.0`), C#, Entity Framework Core 8.0 (`Microsoft.EntityFrameworkCore.SqlServer`), ASP.NET Core OData 8.2 (`Microsoft.AspNetCore.OData`)
- **Database**: Microsoft SQL Server
- **Web Server**: Internet Information Services (IIS) with ASP.NET Core Module V2 (`InProcess` hosting model), supporting virtual application subpaths (e.g., `http://<server>/hr/`)
- **Frontend**: Vanilla HTML5 / CSS3 / ES6 JavaScript (`wwwroot/`) with zero external CDN dependencies

```
hr-poc/
├── data/                                                  # Baseline CSV template files (semicolon-delimited)
│   ├── MDC-Department.csv                                 # 12 departments (Levels 1-3, root: My Demo Corp)
│   ├── MDC-Costcenter.csv                                 # 12 cost centers (Levels 1-3)
│   ├── MDC-Locations.csv                                  # 8 global office locations (US, UK, DE, CA, AU)
│   └── MDC-HR-Data.csv                                    # 50 sample employees (Personnel # 10001-10050)
├── deploy/
│   ├── Install-HrPoc.ps1                                  # Interactive prerequisite checker & IIS/SQL installer (v1.3.0)
│   ├── Publish-ToIIS.ps1                                  # Non-interactive IIS + SQL deployment script (v1.3.0)
│   └── sql/
│       └── 01-create-schema.sql                           # Standalone T-SQL database schema reference
├── postman/
│   ├── My-Demo-Corp-HR-PoC.postman_collection.json        # Parameterized Postman collection (Bearer Auth, OIDC & OData v4)
│   └── My-Demo-Corp-HR-PoC.postman_environment.json       # Sample Postman environment ({{baseUrl}}, {{bearerToken}}, etc.)
└── src/
    └── HrPoc.Web/                                         # ASP.NET Core 8.0 Web Application
        ├── Controllers/                                   # REST API (/api/*), Auth, Settings & OData v4 controllers
        ├── Data/                                          # EF Core DbContext & CSV Data Seeder
        ├── Middleware/                                    # OIDC Authorization Middleware (Web & OData)
        ├── Models/                                        # Employee, Department, CostCenter, Location, InstallationConfig
        ├── Services/                                      # OidcAuthService (Discovery, Token Validation, Scope Checking)
        └── wwwroot/                                       # Single-Page Web UI (index.html, settings.html, CSS, JS)
```

---

## OpenID Connect (OIDC) & OAuth 2.0 Authorization Server (v1.3)

My Demo Corp HR PoC v1.3 introduces enterprise identity protection for both the interactive Web UI and OData v4 / REST APIs, plus `.NET 8 / 9 / 10+` runtime compatibility.

### 1. Web UI Single Sign-On (SSO) & Lockout Prevention
- **SSO Gate**: When `UseOidcForWeb` is enabled, unauthenticated visitors accessing `index.html` are presented with an SSO Gate prompt to "Sign in with SSO" (redirecting to the configured OpenID Connect identity provider via Authorization Code + PKCE flow).
- **Lockout Prevention**: The SSO Gate features a prominent **Settings** link directing to `/hr/settings.html`. The Settings interface and backend configuration APIs (`/api/settings/*`) remain accessible to ensure administrators can update identity provider credentials or disable OIDC in the event of an identity provider misconfiguration.

### 2. OData v4 API Bearer Authentication
- When `UseOidcForOData` is enabled, all requests to `/hr/odata/*` require a valid HTTP header:
  ```http
  Authorization: Bearer <access_token>
  ```
- The backend dynamically queries the Identity Provider's OpenID Connect discovery endpoint (`<OidcAuthority>/.well-known/openid-configuration`) to retrieve signing keys (`jwks_uri`), valid issuers, and supported signing algorithms (`RS256`, `ES256`).

### 3. OAuth 2.0 API Authorization Server & Granular Entity Audiences
When connecting via an OAuth 2.0 API Authorization Server (supporting RFC 8707 Resource Indicators / JWT access tokens):
- **Resource & Per-Entity Audience (`aud` / `resource`)**: Validates incoming token audiences against the configured root resource identifier (`<base>`) or individual entity audiences (`<base>/odata/Employees`, `Departments`, `CostCenters`, `Locations` and `/api/employees`, `departments`, `costcenters`, `locations`). If a token only grants access to a subset of entities, the Web UI isolates restricted tabs with an access notice rather than blocking permitted data.
- **Fine-Grained Scopes**:
  - `hr:read` (or custom configured read scope): Required for `GET` queries on all OData v4 entities and metadata.
  - `hr:write` (or custom configured write scope): Required for state-modifying operations (`POST`, `PATCH`, `PUT`, `DELETE`).
- **Custom Claims**: Optional verification of custom identity claims (e.g., `department` == `IT`, or `role` == `HRAdmin`).
- **Enforcement Policy**:
  - **Enforce Authorization Server**: Strictly enforces audience match, scopes (`hr:read` / `hr:write`), and custom claims.
  - **Allow Any Authenticated User**: Accepts any valid token issued by the configured OIDC Authority, allowing all authenticated identities to access the API.

### 4. Admin Settings & Diagnostic Suite (`/hr/settings.html`)
The dedicated Settings UI provides real-time administrative controls:
- Toggle Web UI SSO and OData v4 Bearer Auth independently.
- Configure OIDC Authority, Client ID, Client Secret, Redirect URI, and Scopes, with 1-click Copy helpers for Redirect URIs.
- Configure Authorization Server Issuer, Audience, Scopes, and Custom Claim filters, plus ready-to-copy fields for Resource Identifier and comma-delimited Audiences (All, OData v4 Only, REST Only).
- **Test OIDC Discovery**: Tests connectivity and parses discovery metadata from the Identity Provider.
- **Test Client Credentials**: Obtains a sample M2M access token server-side and verifies token issuance.
- **Token Inspector**: Decodes any JWT token, validates its signature against live JWKS keys, and verifies effective read/write permissions.

---

## System Requirements

| Component | Requirement | Notes |
| :--- | :--- | :--- |
| **Operating System** | Windows Server 2019 / 2022 / 2025 or Windows 10 / 11 | Administrator privileges required for IIS installation |
| **Web Server** | IIS (`Web-Server`) + Management Scripting Tools | Automatically installed by `Install-HrPoc.ps1` if missing |
| **.NET Runtime** | ASP.NET Core 8.0, 9.0, or 10.0+ Hosting Bundle (`AspNetCoreModuleV2` + `Microsoft.AspNetCore.App >= 8.0`) | Uses `LatestMajor` roll-forward; `Install-HrPoc.ps1` checks shared runtimes and offers Hosting Bundle setup if missing |
| **Database** | Microsoft SQL Server (2016 / 2019 / 2022 — Express, Developer, Standard, or Enterprise) | Local or remote instance; Mixed Mode authentication is used for the dedicated app login |
| **.NET SDK** *(Build only)* | .NET 8.0 SDK | Required only when compiling from source; `Install-HrPoc.ps1` can download a portable SDK automatically |

---

## Quick Start: Interactive Deployment to IIS

The repository includes an interactive PowerShell installer ([`deploy/Install-HrPoc.ps1`](deploy/Install-HrPoc.ps1)) that automates prerequisite verification, database provisioning, and IIS configuration.

### 1. Clone or Copy the Repository

Copy the project folder to your target Windows Server (or clone via Git):

```powershell
git clone https://github.com/dmy2000/my-demo-corp-hr-poc.git
cd my-demo-corp-hr-poc
```

### 2. Run the Interactive Installer

Open **Windows PowerShell as Administrator** and run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\deploy\Install-HrPoc.ps1
```

### What the Installer / Updater Does Step-by-Step:

1. **Step 1 — Prerequisite Check**:
   - Verifies that IIS (`W3SVC`) and the `WebAdministration` PowerShell module are installed (offers one-click installation via `Install-WindowsFeature` / `Enable-WindowsOptionalFeature` if missing).
   - Verifies that `AspNetCoreModuleV2` (ASP.NET Core Hosting Bundle) is registered in IIS (offers automatic repair from package cache or download of the official Microsoft ASP.NET Core 8.0 Hosting Bundle).
   - Checks for compiled binaries in `./publish`. If not present, compiles a compact framework-dependent release (`dotnet publish -c Release -o .\publish`) using the local .NET 8 SDK (or downloads a portable .NET 8 SDK if needed).
2. **Step 2 & 3 — SQL Connection & Smart Mode Detection**:
   - Prompts for the **SQL Server Hostname/IP**, optional **Instance Name**, **Administrator Authentication Method** (`WindowsUPN`, `SqlLogin`, or `CurrentWindows`), and **Target Database Name** (default: `HrPocDb`).
   - Connects to SQL Server, verifies admin permissions, and checks if the database exists with the `dbo.InstallationConfig` table:
     - **Update Mode (Existing Installation)**: Automatically loads existing IIS Site Name, Virtual Subpath, Physical Directory, Application Pool, Dedicated SQL User, and previous version. Upgrades table schema to v1.3 with all 15 OIDC columns without overwriting existing settings, confirms the update, and proceeds immediately!
     - **Fresh Install Mode**: Prompts for the dedicated SQL login (default: `hr_app`), password, IIS Site, Virtual Subpath (default: `hr`), and physical installation path.
3. **Step 4 — Safe IIS Shutdown, Deployment & Settings Persistence**:
   - Safely stops the IIS Application Pool and verifies that all file locks (e.g. `w3wp.exe` handles on `HrPoc.Web.dll`) are fully released before copying files.
   - Copies updated application binaries.
   - **Preserves existing CSV files**: If `data/` in the installation directory already contains CSV data (e.g., custom enterprise datasets), the installer leaves them untouched and does not overwrite them with distribution templates.
   - Writes/updates `dbo.InstallationConfig` in the database with the deployed version and IIS parameters.
   - Configures/verifies the IIS Application Pool (`No Managed Code`) and Web Application, then starts the pool and website.
4. **Step 5 — Smoke Test & Health Verification**:
   - Queries `http://localhost/hr/api/dashboard/stats` and verifies `/hr/odata/$metadata` to confirm the application and database are healthy.

---

## Building for Offline / Air-Gapped Servers

If your target Windows Server does not have internet access to download NuGet packages during `dotnet publish`, build the `./publish` folder on a workstation with the .NET 8 SDK before copying the project folder to the server:

```powershell
dotnet publish .\src\HrPoc.Web\HrPoc.Web.csproj -c Release -o .\publish
```

> **Note:** Because the target IIS server runs the ASP.NET Core Hosting Bundle (8.0, 9.0, or 10.0+), the project uses `<RollForward>LatestMajor</RollForward>` and a compact **framework-dependent** publish profile (`~15` application/library files instead of `350+` runtime DLLs). Once `./publish` exists in the project folder, `.\deploy\Install-HrPoc.ps1` will use it directly without requiring the .NET SDK on the server.

---

## Customizing the CSV Template Data (`data/`)

Out of the box, the [`data/`](data/) directory contains synthetic template data for **My Demo Corp**:
- **`MDC-Department.csv`**: **12 departments** across 3 hierarchy levels (`My Demo Corp` -> `Corporate Operations`, `Technology & Engineering`, `Sales & Customer Success` -> 8 functional departments).
- **`MDC-Costcenter.csv`**: **12 cost centers** across 3 hierarchy levels (`10`, `20`, `30` and child cost centers).
- **`MDC-Locations.csv`**: **8 office locations** across 5 countries (`US`, `UK`, `DE`, `CA`, `AU`).
- **`MDC-HR-Data.csv`**: **50 employees** (`Personnel number` `10001`–`10050`).

All CSV files are **UTF-8** encoded and **semicolon-delimited (`;`)**:

| File | Required Columns (Semicolon-Delimited `;`) |
| :--- | :--- |
| `MDC-Department.csv` | `Department;Short name;Department structured name;Parent department structured name;Functional shortcut;Tree level;Manager;Deputy manager;AD container for new hires` |
| `MDC-Costcenter.csv` | `Cost center;Costcenter structured name;Short name;Parent cost center structured name;Company main structure;company country structure;Tree level;Manager` |
| `MDC-Locations.csv` | `Location;Location structured name;Description;Country shortcut;Zip code;City;Street;Phone;Fax;Manager;Assistant manager` |
| `MDC-HR-Data.csv` | `Username;City;Fax;First name;Initials;Last name;Personnel number;Phone;cost center structured name;Department structured name;Location structured name;Street;Zip code` |

During initial seeding (or when clicking **Reset CSV Data** in the Web UI), `CsvDataSeeder` automatically enriches each employee record with deterministic IAM attributes:
- `EmploymentStatus` (`Active`, `OnLeave`, `Terminated` — all department, cost center, and location managers remain `Active`)
- `HireDate` and `TerminationDate`
- `JobTitle` (based on leadership roles or department assignment)
- `Email` (`firstname.lastname@mydemocorp.com`)
- `CreatedAt` and `UpdatedAt` (UTC timestamps for delta synchronization)

---

## OData v4 & REST API Reference

Assuming the application is deployed under `/hr` on IIS (`http://<server>/hr`):

### 1. OData v4 Endpoints (`/hr/odata`)

| Method | Endpoint / Query Example | Description |
| :--- | :--- | :--- |
| `GET` | `/hr/odata` | OData v4 Service Document |
| `GET` | `/hr/odata/$metadata` | OData v4 CSDL XML Schema (`Namespace="MyDemoCorp.HrPoc"`) |
| `GET` | `/hr/odata/Employees?$top=25&$skip=0&$count=true` | Paginated employee query with total count (`MaxTop=50000`, `PageSize=5000`) |
| `GET` | `/hr/odata/Employees?$filter=EmploymentStatus eq 'Active'&$expand=Department,CostCenter,Location` | Filter active employees and expand related entities |
| `GET` | `/hr/odata/Employees?$filter=UpdatedAt gt 2026-01-01T00:00:00Z&$orderby=UpdatedAt desc` | Delta synchronization query by `UpdatedAt` UTC timestamp |
| `GET` | `/hr/odata/Employees(1)` | Get single employee by primary key |
| `POST` | `/hr/odata/Employees` | Create a new employee (resolves `DepartmentId` or `DepartmentStructuredName` automatically) |
| `PATCH` | `/hr/odata/Employees(1)` | Partial update (`Delta<Employee>`, e.g., write-back `Email`, `Phone`, or `EmploymentStatus`) |
| `PUT` | `/hr/odata/Employees(1)` | Full replacement of an employee record |
| `DELETE` | `/hr/odata/Employees(1)` | Delete an employee record |
| `GET / POST / PATCH / PUT / DELETE` | `/hr/odata/Departments`, `/hr/odata/CostCenters`, `/hr/odata/Locations` | Full CRUD and query support for all organizational entities |

### 2. Web UI REST API Endpoints (`/hr/api`)

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/hr/api/dashboard/stats` | Summary KPIs, status breakdown, country list, and 8 most recently updated employees |
| `POST` | `/hr/api/dashboard/reseed` | Resets the SQL Server database back to the `data/MDC-*.csv` files |
| `GET / POST / PUT / DELETE` | `/hr/api/employees` | Search, filter, paginate, create, update, and delete employees |
| `GET` | `/hr/api/employees/lookup?query=doe` | Fast employee autocomplete for manager / deputy / assistant selection |
| `GET` | `/hr/api/employees/next-identifiers?firstName=John&lastName=Doe` | Suggests next available `PersonnelNumber`, `Username`, `Initials`, and `Email` |
| `GET / POST / PUT / DELETE` | `/hr/api/departments`, `/hr/api/costcenters`, `/hr/api/locations` | Full CRUD for reference entities with employee headcount |

### 3. Authentication & Settings REST API Endpoints (`/hr/api`)

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/hr/api/auth/status` | Current authentication state, active user profile, effective scopes, and permission checks |
| `GET` | `/hr/api/auth/login` | Initiates OIDC Authorization Code login flow redirect |
| `GET` | `/hr/api/auth/callback` | Handles OIDC authorization code exchange and sets session cookie |
| `POST` | `/hr/api/auth/logout` | Signs the current user out of the local cookie session |
| `GET` | `/hr/api/settings` | Returns full installation and OIDC / Authorization Server configuration |
| `PUT` | `/hr/api/settings` | Updates OIDC / Authorization Server settings in `dbo.InstallationConfig` |
| `POST` | `/hr/api/settings/test-discovery` | Tests OIDC discovery metadata endpoint connectivity |
| `POST` | `/hr/api/settings/test-token` | Tests client credentials token request against the configured OIDC Authority |
| `POST` | `/hr/api/settings/inspect-token` | Validates JWT token signature, claims, audience, scopes, and permissions |

---

## Testing with Postman

A complete, parameterized Postman collection is included in [`postman/`](postman/):

1. Open Postman and click **Import**.
2. Select [`postman/My-Demo-Corp-HR-PoC.postman_collection.json`](postman/My-Demo-Corp-HR-PoC.postman_collection.json) and [`postman/My-Demo-Corp-HR-PoC.postman_environment.json`](postman/My-Demo-Corp-HR-PoC.postman_environment.json).
3. Update the `baseUrl` variable to match your server (e.g., `http://localhost/hr` or `http://hr-server.corp.example.com/hr`, without a trailing slash).
4. **OIDC & Bearer Authentication Testing (Folder 0)**:
   - Configure `oidcAuthority`, `oidcClientId`, `oidcClientSecret`, and `authzAudience` in your environment.
   - Run **Request M2M Access Token from Token Endpoint**: Its test script automatically saves the returned `access_token` into the `{{bearerToken}}` variable.
   - The collection has collection-level Bearer Auth configured, so all OData v4 and REST requests in subsequent folders automatically include `Authorization: Bearer {{bearerToken}}`!
5. **Entity Lifecycle Testing (Folders 1–6)**:
   - Run requests across all folders (`Service & Metadata`, `Employees`, `Departments`, `CostCenters`, `Locations`, and `Web UI REST API`). Executing any `POST` request automatically stores the new entity ID into `{{createdEmployeeId}}` / `{{createdDepartmentId}}` / etc. so you can immediately test `PATCH`, `PUT`, and `DELETE` in sequence.
