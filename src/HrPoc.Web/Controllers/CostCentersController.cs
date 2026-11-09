using HrPoc.Web.Data;
using HrPoc.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CostCentersController : ControllerBase
{
    private readonly HrDbContext _db;

    public CostCentersController(HrDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetCostCenters([FromQuery] string? search = null, CancellationToken ct = default)
    {
        var query = _db.CostCenters.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c =>
                c.Code.Contains(term) ||
                c.StructuredName.Contains(term) ||
                (c.ShortName != null && c.ShortName.Contains(term)) ||
                (c.CompanyMainStructure != null && c.CompanyMainStructure.Contains(term)) ||
                (c.CompanyCountryStructure != null && c.CompanyCountryStructure.Contains(term)) ||
                (c.ManagerUsername != null && c.ManagerUsername.Contains(term)));
        }

        var items = await query
            .OrderBy(c => c.StructuredName)
            .ThenBy(c => c.ShortName)
            .Select(c => new
            {
                c.Id,
                c.Code,
                c.StructuredName,
                c.ShortName,
                c.ParentStructuredName,
                c.ParentCostCenterId,
                c.CompanyMainStructure,
                c.CompanyCountryStructure,
                c.TreeLevel,
                c.ManagerUsername,
                EmployeeCount = c.Employees.Count(),
                c.CreatedAt,
                c.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCostCenter(int id, CancellationToken ct)
    {
        var c = await _db.CostCenters
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.StructuredName,
                x.ShortName,
                x.ParentStructuredName,
                x.ParentCostCenterId,
                x.CompanyMainStructure,
                x.CompanyCountryStructure,
                x.TreeLevel,
                x.ManagerUsername,
                EmployeeCount = x.Employees.Count(),
                x.CreatedAt,
                x.UpdatedAt
            })
            .FirstOrDefaultAsync(ct);

        if (c == null)
            return NotFound(new { message = $"Cost Center #{id} not found." });

        return Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> CreateCostCenter([FromBody] CostCenterUpsertRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new { message = "Cost Center Code is required." });

        CostCenter? parent = null;
        if (req.ParentCostCenterId.HasValue)
        {
            parent = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ParentCostCenterId.Value, ct);
        }

        string code = req.Code.Trim();
        string structuredName = !string.IsNullOrWhiteSpace(req.StructuredName)
            ? req.StructuredName.Trim()
            : (parent != null ? $"{parent.StructuredName}\\{code}" : code);

        var now = DateTime.UtcNow;
        var cc = new CostCenter
        {
            Code = code,
            StructuredName = structuredName,
            ShortName = EmptyToNull(req.ShortName) ?? code,
            ParentCostCenterId = parent?.Id,
            ParentStructuredName = parent?.StructuredName ?? EmptyToNull(req.ParentStructuredName),
            CompanyMainStructure = EmptyToNull(req.CompanyMainStructure),
            CompanyCountryStructure = EmptyToNull(req.CompanyCountryStructure),
            TreeLevel = parent != null ? parent.TreeLevel + 1 : (req.TreeLevel > 0 ? req.TreeLevel : 1),
            ManagerUsername = EmptyToNull(req.ManagerUsername)?.ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.CostCenters.Add(cc);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetCostCenter), new { id = cc.Id }, cc);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateCostCenter(int id, [FromBody] CostCenterUpsertRequest req, CancellationToken ct)
    {
        var cc = await _db.CostCenters.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cc == null)
            return NotFound(new { message = $"Cost Center #{id} not found." });

        if (req.ParentCostCenterId == id)
            return BadRequest(new { message = "A cost center cannot be its own parent." });

        CostCenter? parent = null;
        if (req.ParentCostCenterId.HasValue)
        {
            parent = await _db.CostCenters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ParentCostCenterId.Value, ct);
        }

        string code = req.Code.Trim();
        string structuredName = !string.IsNullOrWhiteSpace(req.StructuredName)
            ? req.StructuredName.Trim()
            : (parent != null ? $"{parent.StructuredName}\\{code}" : cc.StructuredName);

        string oldStructured = cc.StructuredName;

        cc.Code = code;
        cc.StructuredName = structuredName;
        cc.ShortName = EmptyToNull(req.ShortName);
        cc.ParentCostCenterId = parent?.Id;
        cc.ParentStructuredName = parent?.StructuredName ?? EmptyToNull(req.ParentStructuredName);
        cc.CompanyMainStructure = EmptyToNull(req.CompanyMainStructure);
        cc.CompanyCountryStructure = EmptyToNull(req.CompanyCountryStructure);
        cc.TreeLevel = parent != null ? parent.TreeLevel + 1 : (req.TreeLevel > 0 ? req.TreeLevel : cc.TreeLevel);
        cc.ManagerUsername = EmptyToNull(req.ManagerUsername)?.ToUpperInvariant();
        cc.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, structuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees
                .Where(e => e.CostCenterId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.CostCenterStructuredName, structuredName), ct);

            await _db.CostCenters
                .Where(c => c.ParentCostCenterId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentStructuredName, structuredName), ct);
        }

        return Ok(cc);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteCostCenter(int id, CancellationToken ct)
    {
        var cc = await _db.CostCenters.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cc == null)
            return NotFound(new { message = $"Cost Center #{id} not found." });

        bool hasChildren = await _db.CostCenters.AnyAsync(c => c.ParentCostCenterId == id, ct);
        if (hasChildren)
            return BadRequest(new { message = "Cannot delete cost center because it has child cost centers. Reassign or delete children first." });

        await _db.Employees
            .Where(e => e.CostCenterId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.CostCenterId, (int?)null)
                .SetProperty(e => e.CostCenterStructuredName, (string?)null), ct);

        _db.CostCenters.Remove(cc);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = $"Cost Center '{cc.StructuredName}' deleted." });
    }

    private static string? EmptyToNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class CostCenterUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string? StructuredName { get; set; }
    public string? ShortName { get; set; }
    public int? ParentCostCenterId { get; set; }
    public string? ParentStructuredName { get; set; }
    public string? CompanyMainStructure { get; set; }
    public string? CompanyCountryStructure { get; set; }
    public int TreeLevel { get; set; } = 1;
    public string? ManagerUsername { get; set; }
}

