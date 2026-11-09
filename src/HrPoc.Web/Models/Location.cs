using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HrPoc.Web.Models;

public class Location
{
    public int Id { get; set; }

    [Required]
    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(400)]
    public string StructuredName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(16)]
    public string? CountryShortcut { get; set; }

    [MaxLength(32)]
    public string? ZipCode { get; set; }

    [MaxLength(150)]
    public string? City { get; set; }

    [MaxLength(250)]
    public string? Street { get; set; }

    [MaxLength(64)]
    public string? Phone { get; set; }

    [MaxLength(64)]
    public string? Fax { get; set; }

    [MaxLength(64)]
    public string? ManagerUsername { get; set; }

    [MaxLength(64)]
    public string? AssistantManagerUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}

