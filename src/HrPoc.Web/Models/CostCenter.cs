using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HrPoc.Web.Models;

public class CostCenter
{
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string StructuredName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ShortName { get; set; }

    [MaxLength(200)]
    public string? ParentStructuredName { get; set; }

    public int? ParentCostCenterId { get; set; }
    public CostCenter? ParentCostCenter { get; set; }

    [MaxLength(64)]
    public string? CompanyMainStructure { get; set; }

    [MaxLength(64)]
    public string? CompanyCountryStructure { get; set; }

    public int TreeLevel { get; set; } = 1;

    [MaxLength(64)]
    public string? ManagerUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}

