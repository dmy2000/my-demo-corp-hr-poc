using HrPoc.Web.Data;
using HrPoc.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Controllers.OData;

// ============================================================================
// 1. OData v4 Controller: Employees (/odata/Employees)
// ============================================================================
[Route("odata")]
public class ODataEmployeesController : ODataController
{
    private readonly HrDbContext _db;

    public ODataEmployeesController(HrDbContext db)
    {
        _db = db;
    }

    [EnableQuery(MaxTop = 5000, PageSize = 1000, MaxExpansionDepth = 3)]
    [HttpGet("Employees")]
    public IQueryable<Employee> Get()
    {
        return _db.Employees.AsNoTracking();
    }

    [EnableQuery(MaxExpansionDepth = 3)]
    [HttpGet("Employees({key:int})")]
    [HttpGet("Employees/{key:int}")]
    public SingleResult<Employee> Get([FromODataUri] int key)
    {
        var result = _db.Employees.AsNoTracking().Where(e => e.Id == key);
        return SingleResult.Create(result);
    }

    [HttpPost("Employees")]
    public async Task<IActionResult> Post([FromBody] Employee emp, CancellationToken ct)
    {
        if (emp == null)
            return BadRequest("Employee payload is required.");

        if (string.IsNullOrWhiteSpace(emp.Username) || string.IsNullOrWhiteSpace(emp.PersonnelNumber))
            return BadRequest("Username and PersonnelNumber are required.");

        emp.Id = 0;
        emp.Username = emp.Username.Trim().ToUpperInvariant();
        emp.PersonnelNumber = emp.PersonnelNumber.Trim();
        emp.FirstName = (emp.FirstName ?? string.Empty).Trim();
        emp.LastName = (emp.LastName ?? string.Empty).Trim();
        emp.EmploymentStatus = string.IsNullOrWhiteSpace(emp.EmploymentStatus) ? "Active" : emp.EmploymentStatus.Trim();

        if (emp.HireDate == default)
            emp.HireDate = DateOnly.FromDateTime(DateTime.UtcNow);

        if (await _db.Employees.AnyAsync(e => e.Username == emp.Username, ct))
            return Conflict(new { message = $"Employee with Username '{emp.Username}' already exists." });

        if (await _db.Employees.AnyAsync(e => e.PersonnelNumber == emp.PersonnelNumber, ct))
            return Conflict(new { message = $"Employee with PersonnelNumber '{emp.PersonnelNumber}' already exists." });

        await SyncEmployeeOrgLinksAsync(emp, changedProps: null, ct);
        NormalizeTerminationDate(emp);

        var now = DateTime.UtcNow;
        emp.CreatedAt = now;
        emp.UpdatedAt = now;

        _db.Employees.Add(emp);
        await _db.SaveChangesAsync(ct);

        return Created(emp);
    }

    [HttpPatch("Employees({key:int})")]
    [HttpPatch("Employees/{key:int}")]
    public async Task<IActionResult> Patch([FromODataUri] int key, [FromBody] Delta<Employee> delta, CancellationToken ct)
    {
        if (delta == null)
            return BadRequest("Delta payload is required.");

        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (emp == null)
            return NotFound();

        string oldUsername = emp.Username;
        var changedProps = delta.GetChangedPropertyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        delta.Patch(emp);
        emp.Id = key;
        emp.Username = emp.Username.Trim().ToUpperInvariant();
        emp.PersonnelNumber = emp.PersonnelNumber.Trim();

        if (await _db.Employees.AnyAsync(e => e.Id != key && e.Username == emp.Username, ct))
            return Conflict(new { message = $"Username '{emp.Username}' is already assigned to another employee." });

        if (await _db.Employees.AnyAsync(e => e.Id != key && e.PersonnelNumber == emp.PersonnelNumber, ct))
            return Conflict(new { message = $"PersonnelNumber '{emp.PersonnelNumber}' is already assigned to another employee." });

        await SyncEmployeeOrgLinksAsync(emp, changedProps, ct);
        if (changedProps.Contains(nameof(Employee.EmploymentStatus)) && !changedProps.Contains(nameof(Employee.TerminationDate)))
        {
            NormalizeTerminationDate(emp);
        }

        emp.UpdatedAt = DateTime.UtcNow;

        if (!string.Equals(oldUsername, emp.Username, StringComparison.OrdinalIgnoreCase))
        {
            await CascadeEmployeeUsernameChangeAsync(oldUsername, emp.Username, ct);
        }

        await _db.SaveChangesAsync(ct);
        return Updated(emp);
    }

    [HttpPut("Employees({key:int})")]
    [HttpPut("Employees/{key:int}")]
    public async Task<IActionResult> Put([FromODataUri] int key, [FromBody] Employee update, CancellationToken ct)
    {
        if (update == null)
            return BadRequest("Employee payload is required.");

        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (emp == null)
            return NotFound();

        string oldUsername = emp.Username;
        var username = update.Username.Trim().ToUpperInvariant();
        var personnelNumber = update.PersonnelNumber.Trim();

        if (await _db.Employees.AnyAsync(e => e.Id != key && e.Username == username, ct))
            return Conflict(new { message = $"Username '{username}' is already assigned to another employee." });

        if (await _db.Employees.AnyAsync(e => e.Id != key && e.PersonnelNumber == personnelNumber, ct))
            return Conflict(new { message = $"PersonnelNumber '{personnelNumber}' is already assigned to another employee." });

        emp.Username = username;
        emp.PersonnelNumber = personnelNumber;
        emp.FirstName = (update.FirstName ?? string.Empty).Trim();
        emp.LastName = (update.LastName ?? string.Empty).Trim();
        emp.Initials = update.Initials;
        emp.Email = update.Email;
        emp.JobTitle = update.JobTitle;
        emp.EmploymentStatus = string.IsNullOrWhiteSpace(update.EmploymentStatus) ? "Active" : update.EmploymentStatus.Trim();
        if (update.HireDate != default)
            emp.HireDate = update.HireDate;
        emp.TerminationDate = update.TerminationDate;
        emp.Phone = update.Phone;
        emp.Fax = update.Fax;
        emp.Street = update.Street;
        emp.City = update.City;
        emp.ZipCode = update.ZipCode;
        emp.DepartmentId = update.DepartmentId;
        emp.DepartmentStructuredName = update.DepartmentStructuredName;
        emp.CostCenterId = update.CostCenterId;
        emp.CostCenterStructuredName = update.CostCenterStructuredName;
        emp.LocationId = update.LocationId;
        emp.LocationStructuredName = update.LocationStructuredName;
        emp.UpdatedAt = DateTime.UtcNow;

        await SyncEmployeeOrgLinksAsync(emp, changedProps: null, ct);
        NormalizeTerminationDate(emp);

        if (!string.Equals(oldUsername, emp.Username, StringComparison.OrdinalIgnoreCase))
        {
            await CascadeEmployeeUsernameChangeAsync(oldUsername, emp.Username, ct);
        }

        await _db.SaveChangesAsync(ct);
        return Updated(emp);
    }

    [HttpDelete("Employees({key:int})")]
    [HttpDelete("Employees/{key:int}")]
    public async Task<IActionResult> Delete([FromODataUri] int key, CancellationToken ct)
    {
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == key, ct);
        if (emp == null)
            return NotFound();

        string u = emp.Username;
        await _db.Departments.Where(d => d.ManagerUsername == u)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ManagerUsername, (string?)null), ct);
        await _db.Departments.Where(d => d.DeputyManagerUsername == u)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeputyManagerUsername, (string?)null), ct);
        await _db.CostCenters.Where(c => c.ManagerUsername == u)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ManagerUsername, (string?)null), ct);
        await _db.Locations.Where(l => l.ManagerUsername == u)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ManagerUsername, (string?)null), ct);
        await _db.Locations.Where(l => l.AssistantManagerUsername == u)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AssistantManagerUsername, (string?)null), ct);

        _db.Employees.Remove(emp);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private async Task SyncEmployeeOrgLinksAsync(Employee emp, HashSet<string>? changedProps, CancellationToken ct)
    {
        // Department sync (supports setting by DepartmentId OR by DepartmentStructuredName)
        bool deptIdChanged = changedProps == null || changedProps.Contains(nameof(Employee.DepartmentId));
        bool deptNameChanged = changedProps != null && changedProps.Contains(nameof(Employee.DepartmentStructuredName)) && !changedProps.Contains(nameof(Employee.DepartmentId));

        if (deptNameChanged)
        {
            if (string.IsNullOrWhiteSpace(emp.DepartmentStructuredName))
            {
                emp.DepartmentId = null;
                emp.DepartmentStructuredName = null;
            }
            else
            {
                var d = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.StructuredName == emp.DepartmentStructuredName, ct);
                emp.DepartmentId = d?.Id;
                if (d != null) emp.DepartmentStructuredName = d.StructuredName;
            }
        }
        else if (deptIdChanged)
        {
            if (emp.DepartmentId.HasValue)
            {
                var d = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == emp.DepartmentId.Value, ct);
                emp.DepartmentId = d?.Id;
                emp.DepartmentStructuredName = d?.StructuredName;
            }
            else if (changedProps == null && !string.IsNullOrWhiteSpace(emp.DepartmentStructuredName))
            {
                var d = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.StructuredName == emp.DepartmentStructuredName, ct);
                emp.DepartmentId = d?.Id;
                if (d != null) emp.DepartmentStructuredName = d.StructuredName;
            }
            else
            {
                emp.DepartmentStructuredName = null;
            }
        }

        // CostCenter sync
        bool ccIdChanged = changedProps == null || changedProps.Contains(nameof(Employee.CostCenterId));
        bool ccNameChanged = changedProps != null && changedProps.Contains(nameof(Employee.CostCenterStructuredName)) && !changedProps.Contains(nameof(Employee.CostCenterId));

        if (ccNameChanged)
        {
            if (string.IsNullOrWhiteSpace(emp.CostCenterStructuredName))
            {
                emp.CostCenterId = null;
                emp.CostCenterStructuredName = null;
            }
            else
            {
                var c = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(x => x.StructuredName == emp.CostCenterStructuredName, ct);
                emp.CostCenterId = c?.Id;
                if (c != null) emp.CostCenterStructuredName = c.StructuredName;
            }
        }
        else if (ccIdChanged)
        {
            if (emp.CostCenterId.HasValue)
            {
                var c = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == emp.CostCenterId.Value, ct);
                emp.CostCenterId = c?.Id;
                emp.CostCenterStructuredName = c?.StructuredName;
            }
            else if (changedProps == null && !string.IsNullOrWhiteSpace(emp.CostCenterStructuredName))
            {
                var c = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(x => x.StructuredName == emp.CostCenterStructuredName, ct);
                emp.CostCenterId = c?.Id;
                if (c != null) emp.CostCenterStructuredName = c.StructuredName;
            }
            else
            {
                emp.CostCenterStructuredName = null;
            }
        }

        // Location sync
        bool locIdChanged = changedProps == null || changedProps.Contains(nameof(Employee.LocationId));
        bool locNameChanged = changedProps != null && changedProps.Contains(nameof(Employee.LocationStructuredName)) && !changedProps.Contains(nameof(Employee.LocationId));

        if (locNameChanged)
        {
            if (string.IsNullOrWhiteSpace(emp.LocationStructuredName))
            {
                emp.LocationId = null;
                emp.LocationStructuredName = null;
            }
            else
            {
                var l = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.StructuredName == emp.LocationStructuredName, ct);
                emp.LocationId = l?.Id;
                if (l != null) emp.LocationStructuredName = l.StructuredName;
            }
        }
        else if (locIdChanged)
        {
            if (emp.LocationId.HasValue)
            {
                var l = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == emp.LocationId.Value, ct);
                emp.LocationId = l?.Id;
                emp.LocationStructuredName = l?.StructuredName;
            }
            else if (changedProps == null && !string.IsNullOrWhiteSpace(emp.LocationStructuredName))
            {
                var l = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.StructuredName == emp.LocationStructuredName, ct);
                emp.LocationId = l?.Id;
                if (l != null) emp.LocationStructuredName = l.StructuredName;
            }
            else
            {
                emp.LocationStructuredName = null;
            }
        }
    }

    private async Task CascadeEmployeeUsernameChangeAsync(string oldUsername, string newUsername, CancellationToken ct)
    {
        await _db.Departments.Where(d => d.ManagerUsername == oldUsername)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ManagerUsername, newUsername), ct);
        await _db.Departments.Where(d => d.DeputyManagerUsername == oldUsername)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeputyManagerUsername, newUsername), ct);
        await _db.CostCenters.Where(c => c.ManagerUsername == oldUsername)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ManagerUsername, newUsername), ct);
        await _db.Locations.Where(l => l.ManagerUsername == oldUsername)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ManagerUsername, newUsername), ct);
        await _db.Locations.Where(l => l.AssistantManagerUsername == oldUsername)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AssistantManagerUsername, newUsername), ct);
    }

    private static void NormalizeTerminationDate(Employee emp)
    {
        if (string.Equals(emp.EmploymentStatus, "Terminated", StringComparison.OrdinalIgnoreCase))
        {
            emp.TerminationDate ??= DateOnly.FromDateTime(DateTime.UtcNow);
        }
        else if (string.Equals(emp.EmploymentStatus, "Active", StringComparison.OrdinalIgnoreCase))
        {
            emp.TerminationDate = null;
        }
    }
}

// ============================================================================
// 2. OData v4 Controller: Departments (/odata/Departments)
// ============================================================================
[Route("odata")]
public class ODataDepartmentsController : ODataController
{
    private readonly HrDbContext _db;

    public ODataDepartmentsController(HrDbContext db)
    {
        _db = db;
    }

    [EnableQuery(MaxTop = 1000, MaxExpansionDepth = 3)]
    [HttpGet("Departments")]
    public IQueryable<Department> Get()
    {
        return _db.Departments.AsNoTracking();
    }

    [EnableQuery(MaxExpansionDepth = 3)]
    [HttpGet("Departments({key:int})")]
    [HttpGet("Departments/{key:int}")]
    public SingleResult<Department> Get([FromODataUri] int key)
    {
        return SingleResult.Create(_db.Departments.AsNoTracking().Where(d => d.Id == key));
    }

    [HttpPost("Departments")]
    public async Task<IActionResult> Post([FromBody] Department dept, CancellationToken ct)
    {
        if (dept == null || string.IsNullOrWhiteSpace(dept.Name))
            return BadRequest("Department Name is required.");

        dept.Id = 0;
        dept.Name = dept.Name.Trim();

        Department? parent = null;
        if (dept.ParentDepartmentId.HasValue)
        {
            parent = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dept.ParentDepartmentId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(dept.ParentStructuredName))
        {
            parent = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.StructuredName == dept.ParentStructuredName, ct);
            dept.ParentDepartmentId = parent?.Id;
        }

        if (string.IsNullOrWhiteSpace(dept.StructuredName))
        {
            dept.StructuredName = parent != null ? $"{parent.StructuredName}\\{dept.Name}" : $"My Demo Corp\\{dept.Name}";
        }
        if (parent != null)
        {
            dept.ParentStructuredName = parent.StructuredName;
            dept.TreeLevel = parent.TreeLevel + 1;
        }

        if (await _db.Departments.AnyAsync(d => d.StructuredName == dept.StructuredName, ct))
            return Conflict(new { message = $"Department '{dept.StructuredName}' already exists." });

        var now = DateTime.UtcNow;
        dept.CreatedAt = now;
        dept.UpdatedAt = now;

        _db.Departments.Add(dept);
        await _db.SaveChangesAsync(ct);

        return Created(dept);
    }

    [HttpPatch("Departments({key:int})")]
    [HttpPatch("Departments/{key:int}")]
    public async Task<IActionResult> Patch([FromODataUri] int key, [FromBody] Delta<Department> delta, CancellationToken ct)
    {
        if (delta == null)
            return BadRequest("Delta payload is required.");

        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == key, ct);
        if (dept == null)
            return NotFound();

        string oldStructured = dept.StructuredName;
        delta.Patch(dept);
        dept.Id = key;
        dept.UpdatedAt = DateTime.UtcNow;

        if (await _db.Departments.AnyAsync(d => d.Id != key && d.StructuredName == dept.StructuredName, ct))
            return Conflict(new { message = $"StructuredName '{dept.StructuredName}' is already in use." });

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, dept.StructuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees.Where(e => e.DepartmentId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.DepartmentStructuredName, dept.StructuredName), ct);
            await _db.Departments.Where(d => d.ParentDepartmentId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentStructuredName, dept.StructuredName), ct);
        }

        return Updated(dept);
    }

    [HttpPut("Departments({key:int})")]
    [HttpPut("Departments/{key:int}")]
    public async Task<IActionResult> Put([FromODataUri] int key, [FromBody] Department update, CancellationToken ct)
    {
        if (update == null || string.IsNullOrWhiteSpace(update.Name))
            return BadRequest("Department Name is required.");

        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == key, ct);
        if (dept == null)
            return NotFound();

        string oldStructured = dept.StructuredName;
        dept.Name = update.Name.Trim();
        dept.ShortName = update.ShortName;
        if (!string.IsNullOrWhiteSpace(update.StructuredName))
            dept.StructuredName = update.StructuredName.Trim();
        dept.ParentDepartmentId = update.ParentDepartmentId;
        dept.ParentStructuredName = update.ParentStructuredName;
        dept.FunctionalShortcut = update.FunctionalShortcut;
        if (update.TreeLevel > 0)
            dept.TreeLevel = update.TreeLevel;
        dept.ManagerUsername = update.ManagerUsername;
        dept.DeputyManagerUsername = update.DeputyManagerUsername;
        dept.AdContainerForNewHires = update.AdContainerForNewHires;
        dept.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, dept.StructuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees.Where(e => e.DepartmentId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.DepartmentStructuredName, dept.StructuredName), ct);
            await _db.Departments.Where(d => d.ParentDepartmentId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentStructuredName, dept.StructuredName), ct);
        }

        return Updated(dept);
    }

    [HttpDelete("Departments({key:int})")]
    [HttpDelete("Departments/{key:int}")]
    public async Task<IActionResult> Delete([FromODataUri] int key, CancellationToken ct)
    {
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == key, ct);
        if (dept == null)
            return NotFound();

        if (await _db.Departments.AnyAsync(d => d.ParentDepartmentId == key, ct))
            return BadRequest(new { message = "Cannot delete department because it has child departments." });

        await _db.Employees.Where(e => e.DepartmentId == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.DepartmentId, (int?)null)
                .SetProperty(e => e.DepartmentStructuredName, (string?)null), ct);

        _db.Departments.Remove(dept);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}

// ============================================================================
// 3. OData v4 Controller: CostCenters (/odata/CostCenters)
// ============================================================================
[Route("odata")]
public class ODataCostCentersController : ODataController
{
    private readonly HrDbContext _db;

    public ODataCostCentersController(HrDbContext db)
    {
        _db = db;
    }

    [EnableQuery(MaxTop = 1000, MaxExpansionDepth = 3)]
    [HttpGet("CostCenters")]
    public IQueryable<CostCenter> Get()
    {
        return _db.CostCenters.AsNoTracking();
    }

    [EnableQuery(MaxExpansionDepth = 3)]
    [HttpGet("CostCenters({key:int})")]
    [HttpGet("CostCenters/{key:int}")]
    public SingleResult<CostCenter> Get([FromODataUri] int key)
    {
        return SingleResult.Create(_db.CostCenters.AsNoTracking().Where(c => c.Id == key));
    }

    [HttpPost("CostCenters")]
    public async Task<IActionResult> Post([FromBody] CostCenter cc, CancellationToken ct)
    {
        if (cc == null || string.IsNullOrWhiteSpace(cc.Code))
            return BadRequest("CostCenter Code is required.");

        cc.Id = 0;
        cc.Code = cc.Code.Trim();

        CostCenter? parent = null;
        if (cc.ParentCostCenterId.HasValue)
        {
            parent = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cc.ParentCostCenterId.Value, ct);
        }
        else if (!string.IsNullOrWhiteSpace(cc.ParentStructuredName))
        {
            parent = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.StructuredName == cc.ParentStructuredName, ct);
            cc.ParentCostCenterId = parent?.Id;
        }

        if (string.IsNullOrWhiteSpace(cc.StructuredName))
        {
            cc.StructuredName = parent != null ? $"{parent.StructuredName}\\{cc.Code}" : cc.Code;
        }
        if (parent != null)
        {
            cc.ParentStructuredName = parent.StructuredName;
            cc.TreeLevel = parent.TreeLevel + 1;
        }

        var now = DateTime.UtcNow;
        cc.CreatedAt = now;
        cc.UpdatedAt = now;

        _db.CostCenters.Add(cc);
        await _db.SaveChangesAsync(ct);

        return Created(cc);
    }

    [HttpPatch("CostCenters({key:int})")]
    [HttpPatch("CostCenters/{key:int}")]
    public async Task<IActionResult> Patch([FromODataUri] int key, [FromBody] Delta<CostCenter> delta, CancellationToken ct)
    {
        if (delta == null)
            return BadRequest("Delta payload is required.");

        var cc = await _db.CostCenters.FirstOrDefaultAsync(c => c.Id == key, ct);
        if (cc == null)
            return NotFound();

        string oldStructured = cc.StructuredName;
        delta.Patch(cc);
        cc.Id = key;
        cc.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, cc.StructuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees.Where(e => e.CostCenterId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.CostCenterStructuredName, cc.StructuredName), ct);
            await _db.CostCenters.Where(c => c.ParentCostCenterId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentStructuredName, cc.StructuredName), ct);
        }

        return Updated(cc);
    }

    [HttpPut("CostCenters({key:int})")]
    [HttpPut("CostCenters/{key:int}")]
    public async Task<IActionResult> Put([FromODataUri] int key, [FromBody] CostCenter update, CancellationToken ct)
    {
        if (update == null || string.IsNullOrWhiteSpace(update.Code))
            return BadRequest("CostCenter Code is required.");

        var cc = await _db.CostCenters.FirstOrDefaultAsync(c => c.Id == key, ct);
        if (cc == null)
            return NotFound();

        string oldStructured = cc.StructuredName;
        cc.Code = update.Code.Trim();
        if (!string.IsNullOrWhiteSpace(update.StructuredName))
            cc.StructuredName = update.StructuredName.Trim();
        cc.ShortName = update.ShortName;
        cc.ParentCostCenterId = update.ParentCostCenterId;
        cc.ParentStructuredName = update.ParentStructuredName;
        cc.CompanyMainStructure = update.CompanyMainStructure;
        cc.CompanyCountryStructure = update.CompanyCountryStructure;
        if (update.TreeLevel > 0)
            cc.TreeLevel = update.TreeLevel;
        cc.ManagerUsername = update.ManagerUsername;
        cc.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, cc.StructuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees.Where(e => e.CostCenterId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.CostCenterStructuredName, cc.StructuredName), ct);
            await _db.CostCenters.Where(c => c.ParentCostCenterId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentStructuredName, cc.StructuredName), ct);
        }

        return Updated(cc);
    }

    [HttpDelete("CostCenters({key:int})")]
    [HttpDelete("CostCenters/{key:int}")]
    public async Task<IActionResult> Delete([FromODataUri] int key, CancellationToken ct)
    {
        var cc = await _db.CostCenters.FirstOrDefaultAsync(c => c.Id == key, ct);
        if (cc == null)
            return NotFound();

        if (await _db.CostCenters.AnyAsync(c => c.ParentCostCenterId == key, ct))
            return BadRequest(new { message = "Cannot delete cost center because it has child cost centers." });

        await _db.Employees.Where(e => e.CostCenterId == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.CostCenterId, (int?)null)
                .SetProperty(e => e.CostCenterStructuredName, (string?)null), ct);

        _db.CostCenters.Remove(cc);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}

// ============================================================================
// 4. OData v4 Controller: Locations (/odata/Locations)
// ============================================================================
[Route("odata")]
public class ODataLocationsController : ODataController
{
    private readonly HrDbContext _db;

    public ODataLocationsController(HrDbContext db)
    {
        _db = db;
    }

    [EnableQuery(MaxTop = 1000, MaxExpansionDepth = 3)]
    [HttpGet("Locations")]
    public IQueryable<Location> Get()
    {
        return _db.Locations.AsNoTracking();
    }

    [EnableQuery(MaxExpansionDepth = 3)]
    [HttpGet("Locations({key:int})")]
    [HttpGet("Locations/{key:int}")]
    public SingleResult<Location> Get([FromODataUri] int key)
    {
        return SingleResult.Create(_db.Locations.AsNoTracking().Where(l => l.Id == key));
    }

    [HttpPost("Locations")]
    public async Task<IActionResult> Post([FromBody] Location loc, CancellationToken ct)
    {
        if (loc == null)
            return BadRequest("Location payload is required.");

        loc.Id = 0;
        if (string.IsNullOrWhiteSpace(loc.Name))
        {
            loc.Name = $"{loc.CountryShortcut ?? "XX"} - {loc.City ?? "Unknown"} - {loc.Street ?? ""}".Trim(' ', '-');
        }
        if (string.IsNullOrWhiteSpace(loc.StructuredName))
        {
            loc.StructuredName = loc.Name;
        }

        if (await _db.Locations.AnyAsync(l => l.StructuredName == loc.StructuredName, ct))
            return Conflict(new { message = $"Location '{loc.StructuredName}' already exists." });

        var now = DateTime.UtcNow;
        loc.CreatedAt = now;
        loc.UpdatedAt = now;

        _db.Locations.Add(loc);
        await _db.SaveChangesAsync(ct);

        return Created(loc);
    }

    [HttpPatch("Locations({key:int})")]
    [HttpPatch("Locations/{key:int}")]
    public async Task<IActionResult> Patch([FromODataUri] int key, [FromBody] Delta<Location> delta, CancellationToken ct)
    {
        if (delta == null)
            return BadRequest("Delta payload is required.");

        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == key, ct);
        if (loc == null)
            return NotFound();

        string oldStructured = loc.StructuredName;
        delta.Patch(loc);
        loc.Id = key;
        loc.UpdatedAt = DateTime.UtcNow;

        if (await _db.Locations.AnyAsync(l => l.Id != key && l.StructuredName == loc.StructuredName, ct))
            return Conflict(new { message = $"StructuredName '{loc.StructuredName}' is already in use." });

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, loc.StructuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees.Where(e => e.LocationId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LocationStructuredName, loc.StructuredName), ct);
        }

        return Updated(loc);
    }

    [HttpPut("Locations({key:int})")]
    [HttpPut("Locations/{key:int}")]
    public async Task<IActionResult> Put([FromODataUri] int key, [FromBody] Location update, CancellationToken ct)
    {
        if (update == null)
            return BadRequest("Location payload is required.");

        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == key, ct);
        if (loc == null)
            return NotFound();

        string oldStructured = loc.StructuredName;
        if (!string.IsNullOrWhiteSpace(update.Name))
            loc.Name = update.Name.Trim();
        if (!string.IsNullOrWhiteSpace(update.StructuredName))
            loc.StructuredName = update.StructuredName.Trim();
        loc.Description = update.Description;
        loc.CountryShortcut = update.CountryShortcut;
        loc.ZipCode = update.ZipCode;
        loc.City = update.City;
        loc.Street = update.Street;
        loc.Phone = update.Phone;
        loc.Fax = update.Fax;
        loc.ManagerUsername = update.ManagerUsername;
        loc.AssistantManagerUsername = update.AssistantManagerUsername;
        loc.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, loc.StructuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees.Where(e => e.LocationId == key)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LocationStructuredName, loc.StructuredName), ct);
        }

        return Updated(loc);
    }

    [HttpDelete("Locations({key:int})")]
    [HttpDelete("Locations/{key:int}")]
    public async Task<IActionResult> Delete([FromODataUri] int key, CancellationToken ct)
    {
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == key, ct);
        if (loc == null)
            return NotFound();

        await _db.Employees.Where(e => e.LocationId == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LocationId, (int?)null)
                .SetProperty(e => e.LocationStructuredName, (string?)null), ct);

        _db.Locations.Remove(loc);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}

