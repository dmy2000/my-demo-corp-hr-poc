using HrPoc.Web.Data;
using HrPoc.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EmployeesController : ControllerBase
{
    private readonly HrDbContext _db;

    public EmployeesController(HrDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetEmployees(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int? departmentId = null,
        [FromQuery] int? costCenterId = null,
        [FromQuery] int? locationId = null,
        [FromQuery] string? country = null,
        [FromQuery] string? sortBy = "lastName",
        [FromQuery] string? sortDir = "asc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 200);

        var query = _db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Include(e => e.CostCenter)
            .Include(e => e.Location)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(e =>
                e.Username.Contains(term) ||
                e.PersonnelNumber.Contains(term) ||
                e.FirstName.Contains(term) ||
                e.LastName.Contains(term) ||
                (e.Email != null && e.Email.Contains(term)) ||
                (e.JobTitle != null && e.JobTitle.Contains(term)) ||
                (e.City != null && e.City.Contains(term)) ||
                (e.Phone != null && e.Phone.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(e => e.EmploymentStatus == status);
        }

        if (departmentId.HasValue)
        {
            query = query.Where(e => e.DepartmentId == departmentId.Value);
        }

        if (costCenterId.HasValue)
        {
            query = query.Where(e => e.CostCenterId == costCenterId.Value);
        }

        if (locationId.HasValue)
        {
            query = query.Where(e => e.LocationId == locationId.Value);
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            query = query.Where(e => e.Location != null && e.Location.CountryShortcut == country);
        }

        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        query = (sortBy?.ToLowerInvariant()) switch
        {
            "username" => desc ? query.OrderByDescending(e => e.Username) : query.OrderBy(e => e.Username),
            "personnelnumber" => desc ? query.OrderByDescending(e => e.PersonnelNumber) : query.OrderBy(e => e.PersonnelNumber),
            "firstname" => desc ? query.OrderByDescending(e => e.FirstName).ThenBy(e => e.LastName) : query.OrderBy(e => e.FirstName).ThenBy(e => e.LastName),
            "jobtitle" => desc ? query.OrderByDescending(e => e.JobTitle) : query.OrderBy(e => e.JobTitle),
            "status" => desc ? query.OrderByDescending(e => e.EmploymentStatus) : query.OrderBy(e => e.EmploymentStatus),
            "hiredate" => desc ? query.OrderByDescending(e => e.HireDate) : query.OrderBy(e => e.HireDate),
            "department" => desc ? query.OrderByDescending(e => e.DepartmentStructuredName) : query.OrderBy(e => e.DepartmentStructuredName),
            "costcenter" => desc ? query.OrderByDescending(e => e.CostCenterStructuredName) : query.OrderBy(e => e.CostCenterStructuredName),
            "location" => desc ? query.OrderByDescending(e => e.LocationStructuredName) : query.OrderBy(e => e.LocationStructuredName),
            "updatedat" => desc ? query.OrderByDescending(e => e.UpdatedAt) : query.OrderBy(e => e.UpdatedAt),
            _ => desc
                ? query.OrderByDescending(e => e.LastName).ThenByDescending(e => e.FirstName)
                : query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
        };

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EmployeeDto
            {
                Id = e.Id,
                Username = e.Username,
                PersonnelNumber = e.PersonnelNumber,
                FirstName = e.FirstName,
                LastName = e.LastName,
                Initials = e.Initials,
                Email = e.Email,
                JobTitle = e.JobTitle,
                EmploymentStatus = e.EmploymentStatus,
                HireDate = e.HireDate,
                TerminationDate = e.TerminationDate,
                Phone = e.Phone,
                Fax = e.Fax,
                Street = e.Street,
                City = e.City,
                ZipCode = e.ZipCode,
                DepartmentId = e.DepartmentId,
                DepartmentName = e.Department != null ? e.Department.Name : null,
                DepartmentShortName = e.Department != null ? e.Department.ShortName : null,
                DepartmentStructuredName = e.DepartmentStructuredName,
                CostCenterId = e.CostCenterId,
                CostCenterCode = e.CostCenter != null ? e.CostCenter.Code : null,
                CostCenterShortName = e.CostCenter != null ? e.CostCenter.ShortName : null,
                CostCenterStructuredName = e.CostCenterStructuredName,
                LocationId = e.LocationId,
                LocationName = e.Location != null ? e.Location.Name : null,
                CountryShortcut = e.Location != null ? e.Location.CountryShortcut : null,
                LocationStructuredName = e.LocationStructuredName,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(new
        {
            items,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        });
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> LookupEmployees([FromQuery] string? q = null, [FromQuery] int limit = 30, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 5, 100);
        var query = _db.Employees.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e =>
                e.Username.Contains(term) ||
                e.FirstName.Contains(term) ||
                e.LastName.Contains(term) ||
                e.PersonnelNumber.Contains(term));
        }

        var list = await query
            .OrderBy(e => e.LastName)
            .ThenBy(e => e.FirstName)
            .Take(limit)
            .Select(e => new
            {
                e.Id,
                e.Username,
                e.PersonnelNumber,
                FullName = e.FirstName + " " + e.LastName,
                e.JobTitle,
                e.EmploymentStatus
            })
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("next-identifiers")]
    public async Task<IActionResult> SuggestNextIdentifiers(
        [FromQuery] string? firstName = null,
        [FromQuery] string? lastName = null,
        CancellationToken ct = default)
    {
        var numbers = await _db.Employees
            .AsNoTracking()
            .Select(e => e.PersonnelNumber)
            .ToListAsync(ct);

        int maxNum = 10000;
        foreach (var p in numbers)
        {
            if (int.TryParse(p, out int parsed) && parsed > maxNum)
                maxNum = parsed;
        }
        string nextPersonnelNumber = (maxNum + 1).ToString();

        string cleanLast = OnlyLetters(lastName).ToUpperInvariant();
        string cleanFirst = OnlyLetters(firstName).ToUpperInvariant();

        string suggestedUsername = string.Empty;
        string suggestedInitials = string.Empty;
        string suggestedEmail = string.Empty;

        if (!string.IsNullOrEmpty(cleanLast) || !string.IsNullOrEmpty(cleanFirst))
        {
            suggestedInitials = $"{(cleanFirst.Length > 0 ? cleanFirst[0] : 'X')}{(cleanLast.Length > 0 ? cleanLast[0] : 'X')}";
            string prefix = cleanLast.Length > 7 ? cleanLast[..7] : cleanLast;
            string firstInitial = cleanFirst.Length > 0 ? cleanFirst[..1] : "X";
            string baseUser = $"{prefix}{firstInitial}";

            var existingUsernames = (await _db.Employees
                .AsNoTracking()
                .Where(e => e.Username.StartsWith(baseUser))
                .Select(e => e.Username)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existingUsernames.Contains(baseUser))
            {
                suggestedUsername = baseUser;
            }
            else
            {
                int suffix = 1;
                while (existingUsernames.Contains($"{baseUser}{suffix}"))
                    suffix++;
                suggestedUsername = $"{baseUser}{suffix}";
            }

            suggestedEmail = $"{cleanFirst.ToLowerInvariant()}.{cleanLast.ToLowerInvariant()}@mydemocorp.com";
        }

        return Ok(new
        {
            personnelNumber = nextPersonnelNumber,
            username = suggestedUsername,
            initials = suggestedInitials,
            email = suggestedEmail
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetEmployee(int id, CancellationToken ct)
    {
        var e = await _db.Employees
            .AsNoTracking()
            .Include(x => x.Department)
            .Include(x => x.CostCenter)
            .Include(x => x.Location)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (e == null)
            return NotFound(new { message = $"Employee #{id} not found." });

        var managedDepartments = await _db.Departments
            .AsNoTracking()
            .Where(d => d.ManagerUsername == e.Username || d.DeputyManagerUsername == e.Username)
            .Select(d => new { d.Id, d.Name, d.StructuredName, Role = d.ManagerUsername == e.Username ? "Manager" : "Deputy Manager" })
            .ToListAsync(ct);

        var managedCostCenters = await _db.CostCenters
            .AsNoTracking()
            .Where(c => c.ManagerUsername == e.Username)
            .Select(c => new { c.Id, c.Code, c.ShortName, c.StructuredName })
            .ToListAsync(ct);

        var managedLocations = await _db.Locations
            .AsNoTracking()
            .Where(l => l.ManagerUsername == e.Username || l.AssistantManagerUsername == e.Username)
            .Select(l => new { l.Id, l.Name, l.StructuredName, Role = l.ManagerUsername == e.Username ? "Manager" : "Assistant Manager" })
            .ToListAsync(ct);

        return Ok(new
        {
            employee = new EmployeeDto
            {
                Id = e.Id,
                Username = e.Username,
                PersonnelNumber = e.PersonnelNumber,
                FirstName = e.FirstName,
                LastName = e.LastName,
                Initials = e.Initials,
                Email = e.Email,
                JobTitle = e.JobTitle,
                EmploymentStatus = e.EmploymentStatus,
                HireDate = e.HireDate,
                TerminationDate = e.TerminationDate,
                Phone = e.Phone,
                Fax = e.Fax,
                Street = e.Street,
                City = e.City,
                ZipCode = e.ZipCode,
                DepartmentId = e.DepartmentId,
                DepartmentName = e.Department?.Name,
                DepartmentShortName = e.Department?.ShortName,
                DepartmentStructuredName = e.DepartmentStructuredName,
                CostCenterId = e.CostCenterId,
                CostCenterCode = e.CostCenter?.Code,
                CostCenterShortName = e.CostCenter?.ShortName,
                CostCenterStructuredName = e.CostCenterStructuredName,
                LocationId = e.LocationId,
                LocationName = e.Location?.Name,
                CountryShortcut = e.Location?.CountryShortcut,
                LocationStructuredName = e.LocationStructuredName,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt
            },
            managedDepartments,
            managedCostCenters,
            managedLocations
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateEmployee([FromBody] EmployeeUpsertRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.PersonnelNumber))
            return BadRequest(new { message = "Username and Personnel Number are required." });

        var username = req.Username.Trim().ToUpperInvariant();
        var personnelNumber = req.PersonnelNumber.Trim();

        if (await _db.Employees.AnyAsync(e => e.Username == username, ct))
            return BadRequest(new { message = $"Username '{username}' already exists." });

        if (await _db.Employees.AnyAsync(e => e.PersonnelNumber == personnelNumber, ct))
            return BadRequest(new { message = $"Personnel Number '{personnelNumber}' already exists." });

        var now = DateTime.UtcNow;
        var emp = new Employee
        {
            Username = username,
            PersonnelNumber = personnelNumber,
            FirstName = req.FirstName.Trim(),
            LastName = req.LastName.Trim(),
            Initials = EmptyToNull(req.Initials),
            Email = EmptyToNull(req.Email),
            JobTitle = EmptyToNull(req.JobTitle),
            EmploymentStatus = string.IsNullOrWhiteSpace(req.EmploymentStatus) ? "Active" : req.EmploymentStatus.Trim(),
            HireDate = req.HireDate ?? DateOnly.FromDateTime(now),
            TerminationDate = req.TerminationDate,
            Phone = EmptyToNull(req.Phone),
            Fax = EmptyToNull(req.Fax),
            Street = EmptyToNull(req.Street),
            City = EmptyToNull(req.City),
            ZipCode = EmptyToNull(req.ZipCode),
            CreatedAt = now,
            UpdatedAt = now
        };

        await SyncOrganizationalLinksAsync(emp, req, ct);
        NormalizeTerminationDate(emp);

        _db.Employees.Add(emp);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetEmployee), new { id = emp.Id }, new { emp.Id, emp.Username, emp.PersonnelNumber });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateEmployee(int id, [FromBody] EmployeeUpsertRequest req, CancellationToken ct)
    {
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (emp == null)
            return NotFound(new { message = $"Employee #{id} not found." });

        var username = req.Username.Trim().ToUpperInvariant();
        var personnelNumber = req.PersonnelNumber.Trim();

        if (await _db.Employees.AnyAsync(e => e.Id != id && e.Username == username, ct))
            return BadRequest(new { message = $"Username '{username}' is already assigned to another employee." });

        if (await _db.Employees.AnyAsync(e => e.Id != id && e.PersonnelNumber == personnelNumber, ct))
            return BadRequest(new { message = $"Personnel Number '{personnelNumber}' is already assigned to another employee." });

        string oldUsername = emp.Username;

        emp.Username = username;
        emp.PersonnelNumber = personnelNumber;
        emp.FirstName = req.FirstName.Trim();
        emp.LastName = req.LastName.Trim();
        emp.Initials = EmptyToNull(req.Initials);
        emp.Email = EmptyToNull(req.Email);
        emp.JobTitle = EmptyToNull(req.JobTitle);
        emp.EmploymentStatus = string.IsNullOrWhiteSpace(req.EmploymentStatus) ? "Active" : req.EmploymentStatus.Trim();
        if (req.HireDate.HasValue)
            emp.HireDate = req.HireDate.Value;
        emp.TerminationDate = req.TerminationDate;
        emp.Phone = EmptyToNull(req.Phone);
        emp.Fax = EmptyToNull(req.Fax);
        emp.Street = EmptyToNull(req.Street);
        emp.City = EmptyToNull(req.City);
        emp.ZipCode = EmptyToNull(req.ZipCode);
        emp.UpdatedAt = DateTime.UtcNow;

        await SyncOrganizationalLinksAsync(emp, req, ct);
        NormalizeTerminationDate(emp);

        // If Username changed, keep manager references in sync
        if (!string.Equals(oldUsername, username, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Departments.Where(d => d.ManagerUsername == oldUsername)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ManagerUsername, username), ct);
            await _db.Departments.Where(d => d.DeputyManagerUsername == oldUsername)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeputyManagerUsername, username), ct);
            await _db.CostCenters.Where(c => c.ManagerUsername == oldUsername)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ManagerUsername, username), ct);
            await _db.Locations.Where(l => l.ManagerUsername == oldUsername)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ManagerUsername, username), ct);
            await _db.Locations.Where(l => l.AssistantManagerUsername == oldUsername)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.AssistantManagerUsername, username), ct);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { emp.Id, emp.Username, emp.PersonnelNumber, emp.UpdatedAt });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteEmployee(int id, CancellationToken ct)
    {
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (emp == null)
            return NotFound(new { message = $"Employee #{id} not found." });

        // Clear any manager references pointing to this username
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

        return Ok(new { message = $"Employee '{emp.Username}' deleted." });
    }

    private async Task SyncOrganizationalLinksAsync(Employee emp, EmployeeUpsertRequest req, CancellationToken ct)
    {
        if (req.DepartmentId.HasValue)
        {
            var dept = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.DepartmentId.Value, ct);
            emp.DepartmentId = dept?.Id;
            emp.DepartmentStructuredName = dept?.StructuredName;
        }
        else
        {
            emp.DepartmentId = null;
            emp.DepartmentStructuredName = null;
        }

        if (req.CostCenterId.HasValue)
        {
            var cc = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CostCenterId.Value, ct);
            emp.CostCenterId = cc?.Id;
            emp.CostCenterStructuredName = cc?.StructuredName;
        }
        else
        {
            emp.CostCenterId = null;
            emp.CostCenterStructuredName = null;
        }

        if (req.LocationId.HasValue)
        {
            var loc = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == req.LocationId.Value, ct);
            emp.LocationId = loc?.Id;
            emp.LocationStructuredName = loc?.StructuredName;
        }
        else
        {
            emp.LocationId = null;
            emp.LocationStructuredName = null;
        }
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

    private static string? EmptyToNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string OnlyLetters(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;
        return new string(s.Where(char.IsLetter).ToArray());
    }
}

public class EmployeeDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PersonnelNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Initials { get; set; }
    public string? Email { get; set; }
    public string? JobTitle { get; set; }
    public string EmploymentStatus { get; set; } = "Active";
    public DateOnly HireDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? ZipCode { get; set; }
    public int? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public string? DepartmentShortName { get; set; }
    public string? DepartmentStructuredName { get; set; }
    public int? CostCenterId { get; set; }
    public string? CostCenterCode { get; set; }
    public string? CostCenterShortName { get; set; }
    public string? CostCenterStructuredName { get; set; }
    public int? LocationId { get; set; }
    public string? LocationName { get; set; }
    public string? CountryShortcut { get; set; }
    public string? LocationStructuredName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class EmployeeUpsertRequest
{
    public string Username { get; set; } = string.Empty;
    public string PersonnelNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Initials { get; set; }
    public string? Email { get; set; }
    public string? JobTitle { get; set; }
    public string EmploymentStatus { get; set; } = "Active";
    public DateOnly? HireDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? ZipCode { get; set; }
    public int? DepartmentId { get; set; }
    public int? CostCenterId { get; set; }
    public int? LocationId { get; set; }
}

