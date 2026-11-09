using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HrPoc.Web.Models;

public class Department
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ShortName { get; set; }

    [Required]
    [MaxLength(400)]
    public string StructuredName { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? ParentStructuredName { get; set; }

    public int? ParentDepartmentId { get; set; }
    public Department? ParentDepartment { get; set; }

    [MaxLength(64)]
    public string? FunctionalShortcut { get; set; }

    public int TreeLevel { get; set; } = 1;

    [MaxLength(64)]
    public string? ManagerUsername { get; set; }

    [MaxLength(64)]
    public string? DeputyManagerUsername { get; set; }

    [MaxLength(500)]
    public string? AdContainerForNewHires { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}

