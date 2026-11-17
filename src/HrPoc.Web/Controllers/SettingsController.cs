using HrPoc.Web.Models;
using HrPoc.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly OidcAuthService _authService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(OidcAuthService authService, ILogger<SettingsController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        var cfg = await _authService.GetConfigAsync(ct);

        var computedRedirectUri = !string.IsNullOrWhiteSpace(cfg.OidcRedirectUri)
            ? cfg.OidcRedirectUri.Trim()
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/auth/callback";

        var computedSigninOidcUri = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/signin-oidc";

        var baseAppPath = Request.PathBase.HasValue && !string.IsNullOrEmpty(Request.PathBase.Value)
            ? Request.PathBase.Value
            : "/hr";
        var computedResourceUri = $"{Request.Scheme}://{Request.Host}{baseAppPath}";

        var hasSecret = !string.IsNullOrWhiteSpace(cfg.OidcClientSecret);
        var maskedSecret = hasSecret ? "••••••••" : string.Empty;

        return Ok(new
        {
            iisSiteName = cfg.IISSiteName,
            iisAppSubPath = cfg.IISAppSubPath,
            installPath = cfg.InstallPath,
            appPoolName = cfg.AppPoolName,
            appSqlLogin = cfg.AppSqlLogin,
            appUrl = cfg.AppUrl,
            version = cfg.Version,
            installedAtUtc = cfg.InstalledAtUtc,
            updatedAtUtc = cfg.UpdatedAtUtc,

            useOidcForWeb = cfg.UseOidcForWeb,
            useOidcForOData = cfg.UseOidcForOData,
            useAuthorizationServer = cfg.UseAuthorizationServer,
            oidcAuthority = cfg.OidcAuthority,
            oidcClientId = cfg.OidcClientId,
            hasClientSecret = hasSecret,
            oidcClientSecret = maskedSecret,
            oidcRedirectUri = cfg.OidcRedirectUri,
            oidcScopes = cfg.OidcScopes,
            oidcRequireHttpsMetadata = cfg.OidcRequireHttpsMetadata,
            authzServerIssuer = cfg.AuthzServerIssuer,
            authzAudience = cfg.AuthzAudience,
            authzReadScope = cfg.AuthzReadScope,
            authzWriteScope = cfg.AuthzWriteScope,
            authzRequiredClaimType = cfg.AuthzRequiredClaimType,
            authzRequiredClaimValue = cfg.AuthzRequiredClaimValue,

            computedRedirectUri,
            computedSigninOidcUri,
            computedResourceUri
        });
    }

    [HttpPut]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsDto dto, CancellationToken ct)
    {
        var current = await _authService.GetConfigAsync(ct);

        var updated = new InstallationConfig
        {
            IISSiteName = current.IISSiteName,
            IISAppSubPath = current.IISAppSubPath,
            InstallPath = current.InstallPath,
            AppPoolName = current.AppPoolName,
            AppSqlLogin = current.AppSqlLogin,
            AppUrl = current.AppUrl,
            Version = "1.3.0",
            UseOidcForWeb = dto.UseOidcForWeb,
            UseOidcForOData = dto.UseOidcForOData,
            UseAuthorizationServer = dto.UseAuthorizationServer,
            OidcAuthority = dto.OidcAuthority?.Trim(),
            OidcClientId = dto.OidcClientId?.Trim(),
            OidcRedirectUri = dto.OidcRedirectUri?.Trim(),
            OidcScopes = string.IsNullOrWhiteSpace(dto.OidcScopes) ? "openid profile email" : dto.OidcScopes.Trim(),
            OidcRequireHttpsMetadata = dto.OidcRequireHttpsMetadata,
            AuthzServerIssuer = dto.AuthzServerIssuer?.Trim(),
            AuthzAudience = dto.AuthzAudience?.Trim(),
            AuthzReadScope = dto.AuthzReadScope?.Trim(),
            AuthzWriteScope = dto.AuthzWriteScope?.Trim(),
            AuthzRequiredClaimType = dto.AuthzRequiredClaimType?.Trim(),
            AuthzRequiredClaimValue = dto.AuthzRequiredClaimValue?.Trim()
        };

        // Preserve existing client secret if masked or not provided and keepExistingSecret is true
        if (dto.OidcClientSecret == "••••••••" || (string.IsNullOrWhiteSpace(dto.OidcClientSecret) && dto.KeepExistingSecret))
        {
            updated.OidcClientSecret = current.OidcClientSecret;
        }
        else
        {
            updated.OidcClientSecret = dto.OidcClientSecret?.Trim();
        }

        var saved = await _authService.UpdateConfigAsync(updated, ct);
        _logger.LogInformation("InstallationConfig updated via Settings API (Version {Version})", saved.Version);

        return await GetSettings(ct);
    }

    [HttpPost("test-discovery")]
    public async Task<IActionResult> TestDiscovery([FromBody] TestDiscoveryDto dto, CancellationToken ct)
    {
        var authority = dto.Authority;
        if (string.IsNullOrWhiteSpace(authority))
        {
            var cfg = await _authService.GetConfigAsync(ct);
            authority = cfg.OidcAuthority;
        }

        var result = await _authService.TestDiscoveryAsync(authority, dto.RequireHttpsMetadata, ct);
        return Ok(result);
    }

    [HttpPost("test-token")]
    public async Task<IActionResult> TestToken([FromBody] TestTokenDto dto, CancellationToken ct)
    {
        var cfg = await _authService.GetConfigAsync(ct);

        var authority = !string.IsNullOrWhiteSpace(dto.Authority) ? dto.Authority : cfg.OidcAuthority;
        var clientId = !string.IsNullOrWhiteSpace(dto.ClientId) ? dto.ClientId : cfg.OidcClientId;

        string? secret = dto.ClientSecret;
        if (string.IsNullOrWhiteSpace(secret) || secret == "••••••••")
        {
            secret = cfg.OidcClientSecret;
        }

        var audience = !string.IsNullOrWhiteSpace(dto.AuthzAudience) ? dto.AuthzAudience : cfg.AuthzAudience;
        var scopes = !string.IsNullOrWhiteSpace(dto.Scopes) ? dto.Scopes : "hr:read hr:write";

        var result = await _authService.TestTokenAsync(authority, clientId, secret, audience, scopes, ct);
        return Ok(result);
    }

    [HttpPost("inspect-token")]
    public async Task<IActionResult> InspectToken([FromBody] InspectTokenDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Token))
        {
            return BadRequest(new { error = "Token string is required." });
        }

        var result = await _authService.InspectTokenAsync(dto.Token.Trim(), ct);
        return Ok(result);
    }
}

public class UpdateSettingsDto
{
    public bool UseOidcForWeb { get; set; }
    public bool UseOidcForOData { get; set; }
    public bool UseAuthorizationServer { get; set; }
    public string? OidcAuthority { get; set; }
    public string? OidcClientId { get; set; }
    public string? OidcClientSecret { get; set; }
    public bool KeepExistingSecret { get; set; } = true;
    public string? OidcRedirectUri { get; set; }
    public string? OidcScopes { get; set; }
    public bool OidcRequireHttpsMetadata { get; set; }
    public string? AuthzServerIssuer { get; set; }
    public string? AuthzAudience { get; set; }
    public string? AuthzReadScope { get; set; }
    public string? AuthzWriteScope { get; set; }
    public string? AuthzRequiredClaimType { get; set; }
    public string? AuthzRequiredClaimValue { get; set; }
}

public class TestDiscoveryDto
{
    public string? Authority { get; set; }
    public bool RequireHttpsMetadata { get; set; }
}

public class TestTokenDto
{
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? AuthzAudience { get; set; }
    public string? Scopes { get; set; }
}

public class InspectTokenDto
{
    public string? Token { get; set; }
}
