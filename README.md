# My Demo Corp — HR System (PoC)

An authoritative **Human Resources (HR) Data Source** web application and **Full CRUD OData v4 / REST API** designed for Identity & Access Management (IAM / IGA / IDM) lab environments, Proof-of-Concept (PoC) deployments, and connector integration testing.

---

## Overview & Purpose

When building or demonstrating Identity Management (IDM) solutions, administrators and integrators need a realistic, controllable **Authoritative HR Source** to test identity lifecycle workflows (**Joiner / Mover / Leaver**) without relying on production ERP/HRIS platforms.

**My Demo Corp — HR System (PoC)** provides:
- **Interactive Web UI**: A clean, fast, zero-CDN Single-Page Application (SPA) that works seamlessly in internet-isolated lab networks. Create new hires, transfer employees across departments/cost centers/locations, change employment statuses (`Active`, `OnLeave`, `Terminated`, `Suspended`), manage organizational hierarchies, and track recent modifications.
- **Full CRUD OData v4 API (`/odata`)**: Standard OData v4 endpoints with CSDL XML schema (`/odata/$metadata`) supporting `GET` (`$filter`, `$select`, `$expand`, `$orderby`, `$top`, `$skip`, `$count`), `GET(key)`, `POST`, `PATCH` (`Delta<T>`), `PUT`, and `DELETE` across all four core entities (`Employees`, `Departments`, `CostCenters`, `Locations`).
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
│   ├── Install-HrPoc.ps1                                  # Interactive prerequisite checker & IIS/SQL installer
│   ├── Publish-ToIIS.ps1                                  # Non-interactive IIS + SQL deployment script
│   └── sql/
│       └── 01-create-schema.sql                           # Standalone T-SQL database schema reference
├── postman/
│   ├── My-Demo-Corp-HR-PoC.postman_collection.json        # Parameterized Postman collection (OData v4 & REST)
│   └── My-Demo-Corp-HR-PoC.postman_environment.json       # Sample Postman environment ({{baseUrl}})
└── src/
    └── HrPoc.Web/                                         # ASP.NET Core 8.0 Web Application
        ├── Controllers/                                   # REST API (/api/*) & OData v4 (/odata/*) controllers
        ├── Data/                                          # EF Core DbContext & CSV Data Seeder
        ├── Models/                                        # Employee, Department, CostCenter, Location entities
        └── wwwroot/                                       # Single-Page Web UI (index.html, css/app.css, js/app.js)
```

---

## System Requirements

| Component | Requirement | Notes |
| :--- | :--- | :--- |
| **Operating System** | Windows Server 2019 / 2022 / 2025 or Windows 10 / 11 | Administrator privileges required for IIS installation |
| **Web Server** | IIS (`Web-Server`) + Management Scripting Tools | Automatically installed by `Install-HrPoc.ps1` if missing |
| **.NET Runtime** | Microsoft ASP.NET Core 8.0 Hosting Bundle (`AspNetCoreModuleV2`) | Automatically downloaded/repaired by `Install-HrPoc.ps1` if missing |
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

### What the Installer Does Step-by-Step:

1. **Step 1 — Prerequisite Check**:
   - Verifies that IIS (`W3SVC`) and the `WebAdministration` PowerShell module are installed (offers one-click installation via `Install-WindowsFeature` / `Enable-WindowsOptionalFeature` if missing).
   - Verifies that `AspNetCoreModuleV2` (ASP.NET Core Hosting Bundle) is registered in IIS (offers automatic repair from package cache or download of the official Microsoft ASP.NET Core 8.0 Hosting Bundle).
   - Checks for compiled binaries in `./publish`. If not present, compiles a compact framework-dependent release (`dotnet publish -c Release -o .\publish`) using the local .NET 8 SDK (or downloads a portable .NET 8 SDK if needed).
2. **Step 2 — SQL Server & IIS Configuration Prompts**:
   - Prompts for the **SQL Server Hostname/IP** and optional **Instance Name**.
   - Prompts for the **Administrator Authentication Method** used only during installation to create the database and application login:
     - `[1]` Windows Account in UPN (`admin@corp.example.com`) or `DOMAIN\user` format (uses Win32 `LogonUser` impersonation, so it works even from a non-domain-joined machine)
     - `[2]` SQL Server Admin Login (e.g., `sa`)
     - `[3]` Current Windows Session (`whoami`)
   - Prompts for the target database name (default: `HrPocDb`) and the **dedicated application SQL login** (default: `hr_app`) and password. The web application connects exclusively as `hr_app` (`db_owner` on `HrPocDb`), never as `sa`.
   - Prompts for the IIS Site (default: `Default Web Site`), Virtual Application Subpath (default: `hr` -> `http://<server>/hr/`), and physical path (default: `C:\inetpub\wwwroot\hr`).
3. **Step 3 — SQL Server Validation**:
   - Tests the SQL connection, verifies `sysadmin` (or `dbcreator` + `securityadmin`) privileges, and checks whether SQL Server Mixed Mode Authentication is enabled (offers to enable Mixed Mode and restart the local SQL Server service automatically if needed).
4. **Step 4 — Database Provisioning & IIS Deployment**:
   - Creates `HrPocDb` and provisions the dedicated `hr_app` SQL login/user.
   - Copies application binaries and `data/MDC-*.csv` files to `C:\inetpub\wwwroot\hr`.
   - Generates `appsettings.Production.json` with the `hr_app` connection string.
   - Creates/updates the `HrPocAppPool` (`No Managed Code` / `InProcess`) and the `/hr` IIS Web Application.
5. **Step 5 — Smoke Test & Initial Seeding**:
   - Calls `http://localhost/hr/api/dashboard/stats` (which automatically creates the database tables and seeds the CSV data on first request) and verifies `/hr/odata/$metadata`.

---

## Building for Offline / Air-Gapped Servers

If your target Windows Server does not have internet access to download NuGet packages during `dotnet publish`, build the `./publish` folder on a workstation with the .NET 8 SDK before copying the project folder to the server:

```powershell
dotnet publish .\src\HrPoc.Web\HrPoc.Web.csproj -c Release -o .\publish
```

> **Note:** Because the target IIS server runs the ASP.NET Core 8.0 Hosting Bundle, the project uses a compact **framework-dependent** publish profile (`~15` application/library files instead of `350+` runtime DLLs). Once `./publish` exists in the project folder, `.\deploy\Install-HrPoc.ps1` will use it directly without requiring the .NET SDK on the server.

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
| `GET` | `/hr/odata/Employees?$top=25&$skip=0&$count=true` | Paginated employee query with total count (`MaxTop=5000`) |
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
| `GET` | `/hr/api/employees/next-identifiers?firstName=John&lastName=Doe` | Suggests next available `PersonnelNumber`, `Username`, `Initials`, and `Email` |
| `GET / POST / PUT / DELETE` | `/hr/api/departments`, `/hr/api/costcenters`, `/hr/api/locations` | Full CRUD for reference entities with employee headcount |

---

## Testing with Postman

A complete, parameterized Postman collection is included in [`postman/`](postman/):

1. Open Postman and click **Import**.
2. Select [`postman/My-Demo-Corp-HR-PoC.postman_collection.json`](postman/My-Demo-Corp-HR-PoC.postman_collection.json) (and optionally [`postman/My-Demo-Corp-HR-PoC.postman_environment.json`](postman/My-Demo-Corp-HR-PoC.postman_environment.json)).
3. Update the `baseUrl` variable to match your server (e.g., `http://localhost/hr` or `http://hr-server.corp.example.com/hr`, without a trailing slash).
4. Run requests across all 6 folders (`Service & Metadata`, `Employees`, `Departments`, `CostCenters`, `Locations`, and `Web UI REST API`). Executing any `POST` request automatically stores the new entity ID into `{{createdEmployeeId}}` / `{{createdDepartmentId}}` / etc. so you can immediately test `PATCH`, `PUT`, and `DELETE`.

