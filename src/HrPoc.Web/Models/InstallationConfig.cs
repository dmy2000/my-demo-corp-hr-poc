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
    public string Version { get; set; } = "1.3.0";

    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // --- v1.3 OIDC & Authorization Server Configuration ---

    public bool UseOidcForWeb { get; set; } = false;

    public bool UseOidcForOData { get; set; } = false;

    public bool UseAuthorizationServer { get; set; } = false;

    [MaxLength(512)]
    public string? OidcAuthority { get; set; }

    [MaxLength(256)]
    public string? OidcClientId { get; set; }

    [MaxLength(512)]
    public string? OidcClientSecret { get; set; }

    [MaxLength(512)]
    public string? OidcRedirectUri { get; set; }

    [MaxLength(512)]
    public string? OidcScopes { get; set; } = "openid profile email";

    public bool OidcRequireHttpsMetadata { get; set; } = false;

    [MaxLength(512)]
    public string? AuthzServerIssuer { get; set; }

    [MaxLength(512)]
    public string? AuthzAudience { get; set; }

    [MaxLength(256)]
    public string? AuthzReadScope { get; set; } = "hr:read";

    [MaxLength(256)]
    public string? AuthzWriteScope { get; set; } = "hr:write";

    [MaxLength(256)]
    public string? AuthzRequiredClaimType { get; set; }

    [MaxLength(256)]
    public string? AuthzRequiredClaimValue { get; set; }
}
