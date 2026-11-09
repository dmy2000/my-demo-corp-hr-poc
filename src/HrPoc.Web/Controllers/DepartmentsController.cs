using HrPoc.Web.Data;
using HrPoc.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DepartmentsController : ControllerBase
{
    private readonly HrDbContext _db;

    public DepartmentsController(HrDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetDepartments([FromQuery] string? search = null, CancellationToken ct = default)
    {
        var query = _db.Departments.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(d =>
                d.Name.Contains(term) ||
                d.StructuredName.Contains(term) ||
                (d.ShortName != null && d.ShortName.Contains(term)) ||
                (d.ManagerUsername != null && d.ManagerUsername.Contains(term)) ||
                (d.DeputyManagerUsername != null && d.DeputyManagerUsername.Contains(term)));
        }

        var items = await query
            .OrderBy(d => d.StructuredName)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.ShortName,
                d.StructuredName,
                d.ParentStructuredName,
                d.ParentDepartmentId,
                d.FunctionalShortcut,
                d.TreeLevel,
                d.ManagerUsername,
                d.DeputyManagerUsername,
                d.AdContainerForNewHires,
                EmployeeCount = d.Employees.Count(),
                d.CreatedAt,
                d.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetDepartment(int id, CancellationToken ct)
    {
        var d = await _db.Departments
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.ShortName,
                x.StructuredName,
                x.ParentStructuredName,
                x.ParentDepartmentId,
                x.FunctionalShortcut,
                x.TreeLevel,
                x.ManagerUsername,
                x.DeputyManagerUsername,
                x.AdContainerForNewHires,
                EmployeeCount = x.Employees.Count(),
                x.CreatedAt,
                x.UpdatedAt
            })
            .FirstOrDefaultAsync(ct);

        if (d == null)
            return NotFound(new { message = $"Department #{id} not found." });

        return Ok(d);
    }

    [HttpPost]
    public async Task<IActionResult> CreateDepartment([FromBody] DepartmentUpsertRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Department Name is required." });

        Department? parent = null;
        if (req.ParentDepartmentId.HasValue)
        {
            parent = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.ParentDepartmentId.Value, ct);
        }

        string name = req.Name.Trim();
        string structuredName = !string.IsNullOrWhiteSpace(req.StructuredName)
            ? req.StructuredName.Trim()
            : (parent != null ? $"{parent.StructuredName}\\{name}" : $"My Demo Corp\\{name}");

        if (await _db.Departments.AnyAsync(d => d.StructuredName == structuredName, ct))
            return BadRequest(new { message = $"Department with structured name '{structuredName}' already exists." });

        var now = DateTime.UtcNow;
        var dept = new Department
        {
            Name = name,
            ShortName = EmptyToNull(req.ShortName),
            StructuredName = structuredName,
            ParentDepartmentId = parent?.Id,
            ParentStructuredName = parent?.StructuredName ?? EmptyToNull(req.ParentStructuredName),
            FunctionalShortcut = EmptyToNull(req.FunctionalShortcut),
            TreeLevel = parent != null ? parent.TreeLevel + 1 : (req.TreeLevel > 0 ? req.TreeLevel : 2),
            ManagerUsername = EmptyToNull(req.ManagerUsername)?.ToUpperInvariant(),
            DeputyManagerUsername = EmptyToNull(req.DeputyManagerUsername)?.ToUpperInvariant(),
            AdContainerForNewHires = EmptyToNull(req.AdContainerForNewHires),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Departments.Add(dept);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetDepartment), new { id = dept.Id }, dept);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateDepartment(int id, [FromBody] DepartmentUpsertRequest req, CancellationToken ct)
    {
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dept == null)
            return NotFound(new { message = $"Department #{id} not found." });

        if (req.ParentDepartmentId == id)
            return BadRequest(new { message = "A department cannot be its own parent." });

        Department? parent = null;
        if (req.ParentDepartmentId.HasValue)
        {
            parent = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.ParentDepartmentId.Value, ct);
        }

        string name = req.Name.Trim();
        string structuredName = !string.IsNullOrWhiteSpace(req.StructuredName)
            ? req.StructuredName.Trim()
            : (parent != null ? $"{parent.StructuredName}\\{name}" : dept.StructuredName);

        if (await _db.Departments.AnyAsync(d => d.Id != id && d.StructuredName == structuredName, ct))
            return BadRequest(new { message = $"Another department already uses structured name '{structuredName}'." });

        string oldStructured = dept.StructuredName;

        dept.Name = name;
        dept.ShortName = EmptyToNull(req.ShortName);
        dept.StructuredName = structuredName;
        dept.ParentDepartmentId = parent?.Id;
        dept.ParentStructuredName = parent?.StructuredName ?? EmptyToNull(req.ParentStructuredName);
        dept.FunctionalShortcut = EmptyToNull(req.FunctionalShortcut);
        dept.TreeLevel = parent != null ? parent.TreeLevel + 1 : (req.TreeLevel > 0 ? req.TreeLevel : dept.TreeLevel);
        dept.ManagerUsername = EmptyToNull(req.ManagerUsername)?.ToUpperInvariant();
        dept.DeputyManagerUsername = EmptyToNull(req.DeputyManagerUsername)?.ToUpperInvariant();
        dept.AdContainerForNewHires = EmptyToNull(req.AdContainerForNewHires);
        dept.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, structuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees
                .Where(e => e.DepartmentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.DepartmentStructuredName, structuredName), ct);

            await _db.Departments
                .Where(d => d.ParentDepartmentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentStructuredName, structuredName), ct);
        }

        return Ok(dept);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteDepartment(int id, CancellationToken ct)
    {
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dept == null)
            return NotFound(new { message = $"Department #{id} not found." });

        bool hasChildren = await _db.Departments.AnyAsync(d => d.ParentDepartmentId == id, ct);
        if (hasChildren)
            return BadRequest(new { message = "Cannot delete department because it has child departments. Reassign or delete child departments first." });

        await _db.Employees
            .Where(e => e.DepartmentId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.DepartmentId, (int?)null)
                .SetProperty(e => e.DepartmentStructuredName, (string?)null), ct);

        _db.Departments.Remove(dept);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = $"Department '{dept.StructuredName}' deleted." });
    }

    private static string? EmptyToNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class DepartmentUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? StructuredName { get; set; }
    public int? ParentDepartmentId { get; set; }
    public string? ParentStructuredName { get; set; }
    public string? FunctionalShortcut { get; set; }
    public int TreeLevel { get; set; } = 2;
    public string? ManagerUsername { get; set; }
    public string? DeputyManagerUsername { get; set; }
    public string? AdContainerForNewHires { get; set; }
}

