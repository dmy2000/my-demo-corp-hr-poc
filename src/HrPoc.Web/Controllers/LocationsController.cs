using HrPoc.Web.Data;
using HrPoc.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LocationsController : ControllerBase
{
    private readonly HrDbContext _db;

    public LocationsController(HrDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetLocations(
        [FromQuery] string? search = null,
        [FromQuery] string? country = null,
        CancellationToken ct = default)
    {
        var query = _db.Locations.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l =>
                l.Name.Contains(term) ||
                l.StructuredName.Contains(term) ||
                (l.City != null && l.City.Contains(term)) ||
                (l.Street != null && l.Street.Contains(term)) ||
                (l.Description != null && l.Description.Contains(term)) ||
                (l.ManagerUsername != null && l.ManagerUsername.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            query = query.Where(l => l.CountryShortcut == country);
        }

        var items = await query
            .OrderBy(l => l.CountryShortcut)
            .ThenBy(l => l.City)
            .ThenBy(l => l.Name)
            .Select(l => new
            {
                l.Id,
                l.Name,
                l.StructuredName,
                l.Description,
                l.CountryShortcut,
                l.ZipCode,
                l.City,
                l.Street,
                l.Phone,
                l.Fax,
                l.ManagerUsername,
                l.AssistantManagerUsername,
                EmployeeCount = l.Employees.Count(),
                l.CreatedAt,
                l.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetLocation(int id, CancellationToken ct)
    {
        var l = await _db.Locations
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.StructuredName,
                x.Description,
                x.CountryShortcut,
                x.ZipCode,
                x.City,
                x.Street,
                x.Phone,
                x.Fax,
                x.ManagerUsername,
                x.AssistantManagerUsername,
                EmployeeCount = x.Employees.Count(),
                x.CreatedAt,
                x.UpdatedAt
            })
            .FirstOrDefaultAsync(ct);

        if (l == null)
            return NotFound(new { message = $"Location #{id} not found." });

        return Ok(l);
    }

    [HttpPost]
    public async Task<IActionResult> CreateLocation([FromBody] LocationUpsertRequest req, CancellationToken ct)
    {
        string country = EmptyToNull(req.CountryShortcut)?.ToUpperInvariant() ?? "XX";
        string city = EmptyToNull(req.City) ?? "Unknown";
        string street = EmptyToNull(req.Street) ?? string.Empty;

        string name = !string.IsNullOrWhiteSpace(req.Name)
            ? req.Name.Trim()
            : (!string.IsNullOrEmpty(street) ? $"{country} - {city} - {street}" : $"{country} - {city}");

        string structuredName = !string.IsNullOrWhiteSpace(req.StructuredName)
            ? req.StructuredName.Trim()
            : name;

        if (await _db.Locations.AnyAsync(l => l.StructuredName == structuredName, ct))
            return BadRequest(new { message = $"Location '{structuredName}' already exists." });

        var now = DateTime.UtcNow;
        var loc = new Location
        {
            Name = name,
            StructuredName = structuredName,
            Description = EmptyToNull(req.Description) ?? $"{street}, {req.ZipCode} {city}".Trim(' ', ','),
            CountryShortcut = country,
            ZipCode = EmptyToNull(req.ZipCode),
            City = city,
            Street = EmptyToNull(req.Street),
            Phone = EmptyToNull(req.Phone),
            Fax = EmptyToNull(req.Fax),
            ManagerUsername = EmptyToNull(req.ManagerUsername)?.ToUpperInvariant(),
            AssistantManagerUsername = EmptyToNull(req.AssistantManagerUsername)?.ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Locations.Add(loc);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetLocation), new { id = loc.Id }, loc);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateLocation(int id, [FromBody] LocationUpsertRequest req, CancellationToken ct)
    {
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loc == null)
            return NotFound(new { message = $"Location #{id} not found." });

        string name = !string.IsNullOrWhiteSpace(req.Name) ? req.Name.Trim() : loc.Name;
        string structuredName = !string.IsNullOrWhiteSpace(req.StructuredName) ? req.StructuredName.Trim() : name;

        if (await _db.Locations.AnyAsync(l => l.Id != id && l.StructuredName == structuredName, ct))
            return BadRequest(new { message = $"Another location already uses structured name '{structuredName}'." });

        string oldStructured = loc.StructuredName;

        loc.Name = name;
        loc.StructuredName = structuredName;
        loc.Description = EmptyToNull(req.Description);
        loc.CountryShortcut = EmptyToNull(req.CountryShortcut)?.ToUpperInvariant();
        loc.ZipCode = EmptyToNull(req.ZipCode);
        loc.City = EmptyToNull(req.City);
        loc.Street = EmptyToNull(req.Street);
        loc.Phone = EmptyToNull(req.Phone);
        loc.Fax = EmptyToNull(req.Fax);
        loc.ManagerUsername = EmptyToNull(req.ManagerUsername)?.ToUpperInvariant();
        loc.AssistantManagerUsername = EmptyToNull(req.AssistantManagerUsername)?.ToUpperInvariant();
        loc.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (!string.Equals(oldStructured, structuredName, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Employees
                .Where(e => e.LocationId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LocationStructuredName, structuredName), ct);
        }

        return Ok(loc);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteLocation(int id, CancellationToken ct)
    {
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loc == null)
            return NotFound(new { message = $"Location #{id} not found." });

        await _db.Employees
            .Where(e => e.LocationId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LocationId, (int?)null)
                .SetProperty(e => e.LocationStructuredName, (string?)null), ct);

        _db.Locations.Remove(loc);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = $"Location '{loc.StructuredName}' deleted." });
    }

    private static string? EmptyToNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class LocationUpsertRequest
{
    public string? Name { get; set; }
    public string? StructuredName { get; set; }
    public string? Description { get; set; }
    public string? CountryShortcut { get; set; }
    public string? ZipCode { get; set; }
    public string? City { get; set; }
    public string? Street { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? ManagerUsername { get; set; }
    public string? AssistantManagerUsername { get; set; }
}

