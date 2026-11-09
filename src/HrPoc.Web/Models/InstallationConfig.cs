using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HrPoc.Web.Models;

[Table("InstallationConfig")]
public class InstallationConfig
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string IISSiteName { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string IISAppSubPath { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string InstallPath { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string AppPoolName { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string AppSqlLogin { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? AppUrl { get; set; }

    [Required]
    [MaxLength(64)]
    public string Version { get; set; } = "1.1.2";

    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
