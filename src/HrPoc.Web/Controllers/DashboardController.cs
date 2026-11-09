using HrPoc.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly HrDbContext _db;
    private readonly CsvDataSeeder _seeder;
    private readonly IConfiguration _config;

    public DashboardController(HrDbContext db, CsvDataSeeder seeder, IConfiguration config)
    {
        _db = db;
        _seeder = seeder;
        _config = config;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var totalEmployees = await _db.Employees.CountAsync(ct);
        var activeEmployees = await _db.Employees.CountAsync(e => e.EmploymentStatus == "Active", ct);
        var terminatedEmployees = await _db.Employees.CountAsync(e => e.EmploymentStatus == "Terminated", ct);
        var onLeaveEmployees = await _db.Employees.CountAsync(e => e.EmploymentStatus == "OnLeave", ct);

        var totalDepartments = await _db.Departments.CountAsync(ct);
        var totalCostCenters = await _db.CostCenters.CountAsync(ct);
        var totalLocations = await _db.Locations.CountAsync(ct);

        var countries = await _db.Locations
            .AsNoTracking()
            .Where(l => l.CountryShortcut != null)
            .Select(l => l.CountryShortcut!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        var recentEmployees = await _db.Employees
            .AsNoTracking()
            .OrderByDescending(e => e.UpdatedAt)
            .ThenByDescending(e => e.Id)
            .Take(8)
            .Select(e => new
            {
                e.Id,
                e.Username,
                e.PersonnelNumber,
                FullName = e.FirstName + " " + e.LastName,
                e.JobTitle,
                e.EmploymentStatus,
                e.DepartmentStructuredName,
                e.UpdatedAt
            })
            .ToListAsync(ct);

        string? installedVersion = null;
        try
        {
            installedVersion = await _db.InstallationConfigs
                .AsNoTracking()
                .OrderByDescending(c => c.Id)
                .Select(c => c.Version)
                .FirstOrDefaultAsync(ct);
        }
        catch
        {
            // Table might not exist yet on legacy un-updated databases
        }

        return Ok(new
        {
            totalEmployees,
            activeEmployees,
            terminatedEmployees,
            onLeaveEmployees,
            totalDepartments,
            totalCostCenters,
            totalLocations,
            countries,
            recentEmployees,
            installedVersion = installedVersion ?? "1.1.2",
            databaseProvider = "SqlServer",
            serverTimeUtc = DateTime.UtcNow
        });
    }

    [HttpPost("reseed")]
    public async Task<IActionResult> ReseedFromCsv(CancellationToken ct)
    {
        var result = await _seeder.EnsureSeededAsync(forceReseed: true, ct);
        return Ok(new
        {
            message = "Database successfully reset and re-seeded from MDC CSV files.",
            result
        });
    }
}

