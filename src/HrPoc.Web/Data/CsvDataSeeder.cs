using System.Globalization;
using System.Text;
using HrPoc.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace HrPoc.Web.Data;

public class CsvDataSeeder
{
    private readonly HrDbContext _db;
    private readonly ILogger<CsvDataSeeder> _logger;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public CsvDataSeeder(
        HrDbContext db,
        ILogger<CsvDataSeeder> logger,
        IConfiguration config,
        IWebHostEnvironment env)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _env = env;
    }

    public string ResolveDataDirectory()
    {
        var configured = _config["CsvDataDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullConfigPath = Path.GetFullPath(configured, _env.ContentRootPath);
            if (Directory.Exists(fullConfigPath) && File.Exists(Path.Combine(fullConfigPath, "MDC-HR-Data.csv")))
                return fullConfigPath;
        }

        string[] candidates =
        [
            Path.Combine(_env.ContentRootPath, "data"),
            Path.Combine(_env.ContentRootPath, "..", "data"),
            Path.Combine(_env.ContentRootPath, "..", "..", "data"),
            Path.Combine(AppContext.BaseDirectory, "data")
        ];

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full) && File.Exists(Path.Combine(full, "MDC-HR-Data.csv")))
            {
                return full;
            }
        }

        throw new DirectoryNotFoundException("Could not locate 'data' directory containing MDC-*.csv files.");
    }

    public async Task<SeedResult> EnsureSeededAsync(bool forceReseed = false, CancellationToken ct = default)
    {
        await _db.Database.EnsureCreatedAsync(ct);

        if (!forceReseed && await _db.Employees.AnyAsync(ct))
        {
            return new SeedResult
            {
                WasSeeded = false,
                EmployeesCount = await _db.Employees.CountAsync(ct),
                DepartmentsCount = await _db.Departments.CountAsync(ct),
                CostCentersCount = await _db.CostCenters.CountAsync(ct),
                LocationsCount = await _db.Locations.CountAsync(ct)
            };
        }

        var dataDir = ResolveDataDirectory();
        _logger.LogInformation("Seeding HR PoC database from CSV directory: {DataDir}", dataDir);

        if (forceReseed)
        {
            // Clear existing rows in FK-safe order
            await _db.Employees.ExecuteDeleteAsync(ct);
            await _db.Departments.Where(d => d.ParentDepartmentId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentDepartmentId, (int?)null), ct);
            await _db.Departments.ExecuteDeleteAsync(ct);
            await _db.CostCenters.Where(c => c.ParentCostCenterId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentCostCenterId, (int?)null), ct);
            await _db.CostCenters.ExecuteDeleteAsync(ct);
            await _db.Locations.ExecuteDeleteAsync(ct);
        }

        var now = DateTime.UtcNow;

        // 1. Seed Locations
        var locRows = ReadSemicolonCsv(Path.Combine(dataDir, "MDC-Locations.csv"));
        var locations = new List<Location>(locRows.Count);
        foreach (var row in locRows)
        {
            var loc = new Location
            {
                Name = Clean(Get(row, "Location")) ?? "Unknown",
                StructuredName = Clean(Get(row, "Location structured name")) ?? string.Empty,
                Description = Clean(Get(row, "Description")),
                CountryShortcut = Clean(Get(row, "Country shortcut")),
                ZipCode = Clean(Get(row, "Zip code")),
                City = Clean(Get(row, "City")),
                Street = Clean(Get(row, "Street")),
                Phone = Clean(Get(row, "Phone")),
                Fax = Clean(Get(row, "Fax")),
                ManagerUsername = Clean(Get(row, "Manager")),
                AssistantManagerUsername = Clean(Get(row, "Assistant manager")),
                CreatedAt = now,
                UpdatedAt = now
            };
            if (!string.IsNullOrWhiteSpace(loc.StructuredName))
            {
                locations.Add(loc);
            }
        }
        _db.Locations.AddRange(locations);
        await _db.SaveChangesAsync(ct);

        // 2. Seed Departments (Levels 1..3)
        var deptRows = ReadSemicolonCsv(Path.Combine(dataDir, "MDC-Department.csv"));
        var departments = new List<Department>(deptRows.Count);
        foreach (var row in deptRows)
        {
            int treeLevel = ParseInt(Get(row, "Tree level"), 1);
            if (treeLevel >= 4)
                continue;

            var dept = new Department
            {
                Name = Clean(Get(row, "Department")) ?? "Unknown",
                ShortName = Clean(Get(row, "Short name")),
                StructuredName = Clean(Get(row, "Department structured name")) ?? string.Empty,
                ParentStructuredName = Clean(Get(row, "Parent department structured name")),
                FunctionalShortcut = Clean(Get(row, "Functional shortcut")),
                TreeLevel = treeLevel,
                ManagerUsername = Clean(Get(row, "Manager")),
                DeputyManagerUsername = Clean(Get(row, "Deputy manager")),
                AdContainerForNewHires = Clean(Get(row, "AD container for new hires")),
                CreatedAt = now,
                UpdatedAt = now
            };
            if (!string.IsNullOrWhiteSpace(dept.StructuredName))
            {
                departments.Add(dept);
            }
        }
        _db.Departments.AddRange(departments);
        await _db.SaveChangesAsync(ct);

        var deptByStructured = departments.ToDictionary(d => d.StructuredName, StringComparer.OrdinalIgnoreCase);
        foreach (var dept in departments)
        {
            if (!string.IsNullOrWhiteSpace(dept.ParentStructuredName) &&
                deptByStructured.TryGetValue(dept.ParentStructuredName, out var parentDept))
            {
                dept.ParentDepartmentId = parentDept.Id;
            }
        }
        await _db.SaveChangesAsync(ct);

        // 3. Seed Cost Centers
        var ccRows = ReadSemicolonCsv(Path.Combine(dataDir, "MDC-Costcenter.csv"));
        var costCenters = new List<CostCenter>(ccRows.Count);
        foreach (var row in ccRows)
        {
            var cc = new CostCenter
            {
                Code = Clean(Get(row, "Cost center")) ?? string.Empty,
                StructuredName = Clean(Get(row, "Costcenter structured name")) ?? string.Empty,
                ShortName = Clean(Get(row, "Short name")),
                ParentStructuredName = Clean(Get(row, "Parent cost center structured name")),
                CompanyMainStructure = Clean(Get(row, "Company main structure")),
                CompanyCountryStructure = Clean(Get(row, "company country structure")),
                TreeLevel = ParseInt(Get(row, "Tree level"), 1),
                ManagerUsername = Clean(Get(row, "Manager")),
                CreatedAt = now,
                UpdatedAt = now
            };
            if (!string.IsNullOrWhiteSpace(cc.StructuredName))
            {
                costCenters.Add(cc);
            }
        }
        _db.CostCenters.AddRange(costCenters);
        await _db.SaveChangesAsync(ct);

        var ccByStructuredList = costCenters
            .GroupBy(c => c.StructuredName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var cc in costCenters)
        {
            if (!string.IsNullOrWhiteSpace(cc.ParentStructuredName) &&
                ccByStructuredList.TryGetValue(cc.ParentStructuredName, out var parentCandidates))
            {
                cc.ParentCostCenterId = parentCandidates[0].Id;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Collect all leadership usernames so managers always remain Active
        var managerRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in locations)
        {
            if (!string.IsNullOrEmpty(l.AssistantManagerUsername))
                managerRoles.TryAdd(l.AssistantManagerUsername, "Assistant Location Manager");
            if (!string.IsNullOrEmpty(l.ManagerUsername))
                managerRoles[l.ManagerUsername] = "Site Manager";
        }
        foreach (var c in costCenters)
        {
            if (!string.IsNullOrEmpty(c.ManagerUsername))
                managerRoles[c.ManagerUsername] = "Cost Center Manager";
        }
        foreach (var d in departments)
        {
            if (!string.IsNullOrEmpty(d.DeputyManagerUsername))
                managerRoles.TryAdd(d.DeputyManagerUsername, $"Deputy Head of {d.Name}");
            if (!string.IsNullOrEmpty(d.ManagerUsername))
                managerRoles[d.ManagerUsername] = d.TreeLevel <= 2 ? $"VP of {d.Name}" : $"Head of {d.Name}";
        }

        var locByStructured = locations.ToDictionary(l => l.StructuredName, StringComparer.OrdinalIgnoreCase);

        // 4. Seed Employees
        var hrRows = ReadSemicolonCsv(Path.Combine(dataDir, "MDC-HR-Data.csv"));
        var employees = new List<Employee>(hrRows.Count);
        var usedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var startDate = new DateOnly(2015, 1, 1);
        var endDate = new DateOnly(2026, 10, 15);
        int totalDaysRange = endDate.DayNumber - startDate.DayNumber;

        foreach (var row in hrRows)
        {
            var username = Clean(Get(row, "Username"));
            var personnelNumber = Clean(Get(row, "Personnel number"));
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(personnelNumber))
                continue;

            var firstName = Clean(Get(row, "First name")) ?? string.Empty;
            var lastName = Clean(Get(row, "Last name")) ?? string.Empty;
            var deptStruct = Clean(Get(row, "Department structured name"));
            var ccStruct = Clean(Get(row, "cost center structured name"));
            var locStruct = Clean(Get(row, "Location structured name"));

            Department? matchedDept = null;
            if (!string.IsNullOrWhiteSpace(deptStruct))
            {
                var segments = deptStruct.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 4)
                {
                    deptStruct = string.Join('\\', segments.Take(3));
                }
                deptByStructured.TryGetValue(deptStruct, out matchedDept);
            }

            Location? matchedLoc = null;
            if (!string.IsNullOrWhiteSpace(locStruct))
                locByStructured.TryGetValue(locStruct, out matchedLoc);

            CostCenter? matchedCc = null;
            if (!string.IsNullOrWhiteSpace(ccStruct) &&
                ccByStructuredList.TryGetValue(ccStruct, out var ccCandidates))
            {
                if (ccCandidates.Count == 1)
                {
                    matchedCc = ccCandidates[0];
                }
                else
                {
                    var country = matchedLoc?.CountryShortcut;
                    matchedCc = ccCandidates.FirstOrDefault(c =>
                        string.Equals(c.CompanyCountryStructure, country, StringComparison.OrdinalIgnoreCase))
                        ?? ccCandidates.Last();
                }
            }

            // Deterministic random generator per employee based on PersonnelNumber & Username
            int seed = StableHash($"{personnelNumber}:{username}");
            var rng = new Random(seed);

            var hireDate = startDate.AddDays(rng.Next(0, totalDaysRange));

            string status = "Active";
            DateOnly? terminationDate = null;

            bool isManager = managerRoles.ContainsKey(username);
            if (!isManager)
            {
                int roll = rng.Next(0, 1000);
                if (roll < 100) // ~10% of non-manager staff Terminated (~3 of 50 total)
                {
                    status = "Terminated";
                    int daysSinceHire = Math.Max(30, endDate.DayNumber - hireDate.DayNumber);
                    int employedDays = rng.Next(15, daysSinceHire + 1);
                    var candidateTerm = hireDate.AddDays(employedDays);
                    terminationDate = candidateTerm > endDate ? endDate : candidateTerm;
                }
                else if (roll < 170) // ~7% of non-manager staff OnLeave (~2 of 50 total)
                {
                    status = "OnLeave";
                }
            }

            string jobTitle = managerRoles.TryGetValue(username, out var mgrTitle)
                ? mgrTitle
                : GenerateJobTitle(deptStruct, rng);

            string email = GenerateEmail(firstName, lastName, username, usedEmails);

            employees.Add(new Employee
            {
                Username = username,
                PersonnelNumber = personnelNumber,
                FirstName = firstName,
                LastName = lastName,
                Initials = Clean(Get(row, "Initials")),
                Email = email,
                JobTitle = jobTitle,
                EmploymentStatus = status,
                HireDate = hireDate,
                TerminationDate = terminationDate,
                Phone = Clean(Get(row, "Phone")),
                Fax = Clean(Get(row, "Fax")),
                Street = Clean(Get(row, "Street")),
                City = Clean(Get(row, "City")),
                ZipCode = Clean(Get(row, "Zip code")),
                DepartmentId = matchedDept?.Id,
                CostCenterId = matchedCc?.Id,
                LocationId = matchedLoc?.Id,
                DepartmentStructuredName = deptStruct,
                CostCenterStructuredName = ccStruct,
                LocationStructuredName = locStruct,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Batch insert employees
        const int batchSize = 1000;
        for (int i = 0; i < employees.Count; i += batchSize)
        {
            var chunk = employees.Skip(i).Take(batchSize);
            _db.Employees.AddRange(chunk);
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Database seeded successfully: {EmpCount} Employees, {DeptCount} Departments, {CcCount} CostCenters, {LocCount} Locations.",
            employees.Count, departments.Count, costCenters.Count, locations.Count);

        return new SeedResult
        {
            WasSeeded = true,
            EmployeesCount = employees.Count,
            DepartmentsCount = departments.Count,
            CostCentersCount = costCenters.Count,
            LocationsCount = locations.Count
        };
    }

    private static string GenerateEmail(string firstName, string lastName, string username, HashSet<string> usedEmails)
    {
        string cleanFirst = SanitizeForEmail(firstName);
        string cleanLast = SanitizeForEmail(lastName);
        string baseEmail = (!string.IsNullOrEmpty(cleanFirst) && !string.IsNullOrEmpty(cleanLast))
            ? $"{cleanFirst}.{cleanLast}@mydemocorp.com"
            : $"{username.ToLowerInvariant()}@mydemocorp.com";

        if (usedEmails.Add(baseEmail))
            return baseEmail;

        string fallback = $"{username.ToLowerInvariant()}@mydemocorp.com";
        usedEmails.Add(fallback);
        return fallback;
    }

    private static string SanitizeForEmail(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    private static string GenerateJobTitle(string? deptStructuredName, Random rng)
    {
        string path = deptStructuredName ?? string.Empty;
        string[] pool;

        if (path.Contains("Executive", StringComparison.OrdinalIgnoreCase))
            pool = ["Executive Operations Advisor", "Corporate Strategy Director", "Chief of Staff"];
        else if (path.Contains("Human Resources", StringComparison.OrdinalIgnoreCase))
            pool = ["HR Business Partner", "Talent Acquisition Specialist", "People Operations Specialist", "Compensation & Benefits Analyst"];
        else if (path.Contains("Finance", StringComparison.OrdinalIgnoreCase) || path.Contains("Accounting", StringComparison.OrdinalIgnoreCase))
            pool = ["Senior Financial Analyst", "Corporate Accountant", "FP&A Specialist", "Treasury & Risk Analyst"];
        else if (path.Contains("Software Engineering", StringComparison.OrdinalIgnoreCase))
            pool = ["Senior Software Engineer", "Backend Platform Engineer", "Full-Stack Developer", "QA Automation Engineer"];
        else if (path.Contains("IT Infrastructure", StringComparison.OrdinalIgnoreCase) || path.Contains("Security", StringComparison.OrdinalIgnoreCase))
            pool = ["Identity & Access Engineer", "Cloud Infrastructure Engineer", "Senior Systems Administrator", "Information Security Analyst"];
        else if (path.Contains("Product", StringComparison.OrdinalIgnoreCase) || path.Contains("Design", StringComparison.OrdinalIgnoreCase))
            pool = ["Senior Product Manager", "UX/UI Designer", "Product Analyst", "Technical Product Specialist"];
        else if (path.Contains("Sales", StringComparison.OrdinalIgnoreCase))
            pool = ["Enterprise Account Executive", "Regional Sales Manager", "Solutions Consultant", "Business Development Representative"];
        else if (path.Contains("Customer Support", StringComparison.OrdinalIgnoreCase) || path.Contains("Customer Success", StringComparison.OrdinalIgnoreCase))
            pool = ["Customer Success Manager", "Senior Technical Support Engineer", "Client Onboarding Specialist", "Support Escalation Lead"];
        else
            pool = ["Operations Specialist", "Business Analyst", "Project Coordinator"];

        return pool[rng.Next(pool.Length)];
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            int hash = 23;
            foreach (char c in text)
            {
                hash = (hash * 31) + c;
            }
            return Math.Abs(hash);
        }
    }

    private static string? Get(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var val) ? val : null;

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        return trimmed;
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(Clean(value), out var result) ? result : fallback;

    private static List<Dictionary<string, string>> ReadSemicolonCsv(string filePath)
    {
        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        if (lines.Length == 0)
            return [];

        var headers = lines[0].Split(';').Select(h => h.Trim()).ToArray();
        var list = new List<Dictionary<string, string>>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(';');
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Length && c < parts.Length; c++)
            {
                dict[headers[c]] = parts[c];
            }
            list.Add(dict);
        }

        return list;
    }
}

public class SeedResult
{
    public bool WasSeeded { get; set; }
    public int EmployeesCount { get; set; }
    public int DepartmentsCount { get; set; }
    public int CostCentersCount { get; set; }
    public int LocationsCount { get; set; }
}
