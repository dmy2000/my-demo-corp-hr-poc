using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HrPoc.Web.Models;

public class Employee
{
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string PersonnelNumber { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? Initials { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(150)]
    public string? JobTitle { get; set; }

    /// <summary>
    /// Active, Terminated, OnLeave, Suspended
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string EmploymentStatus { get; set; } = "Active";

    public DateOnly HireDate { get; set; }

    public DateOnly? TerminationDate { get; set; }

    [MaxLength(64)]
    public string? Phone { get; set; }

    [MaxLength(64)]
    public string? Fax { get; set; }

    [MaxLength(250)]
    public string? Street { get; set; }

    [MaxLength(150)]
    public string? City { get; set; }

    [MaxLength(32)]
    public string? ZipCode { get; set; }

    // Foreign keys to reference entities
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    // Denormalized structured names from CSVs (kept in sync for IDM/OData compatibility)
    [MaxLength(400)]
    public string? DepartmentStructuredName { get; set; }

    [MaxLength(200)]
    public string? CostCenterStructuredName { get; set; }

    [MaxLength(400)]
    public string? LocationStructuredName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

