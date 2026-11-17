using System.Security.Claims;
using HrPoc.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace HrPoc.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly OidcAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(OidcAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var cfg = await _authService.GetConfigAsync(ct);

        var computedRedirectUri = !string.IsNullOrWhiteSpace(cfg.OidcRedirectUri)
            ? cfg.OidcRedirectUri.Trim()
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/auth/callback";

        var isConfigured = !string.IsNullOrWhiteSpace(cfg.OidcAuthority) &&
                           !string.IsNullOrWhiteSpace(cfg.OidcClientId);

        var isAuthenticated = User?.Identity?.IsAuthenticated == true;
        object? userObj = null;

        if (isAuthenticated && User != null)
        {
            var username = User.Identity?.Name
                        ?? User.FindFirst("preferred_username")?.Value
                        ?? User.FindFirst("email")?.Value
                        ?? User.FindFirst("sub")?.Value
                        ?? "User";

            var fullName = User.FindFirst("name")?.Value
                        ?? User.FindFirst(ClaimTypes.Name)?.Value
                        ?? username;

            var email = User.FindFirst("email")?.Value
                     ?? User.FindFirst(ClaimTypes.Email)?.Value
                     ?? string.Empty;

            var sub = User.FindFirst("sub")?.Value
                   ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? username;

            var scopes = _authService.ExtractAllScopes(User);
            var claims = User.Claims
                .Select(c => new { type = c.Type, value = c.Value })
                .ToList();

            var readCheck = _authService.CheckAccess(User, "GET", cfg);
            var writeCheck = _authService.CheckAccess(User, "POST", cfg);

            userObj = new
            {
                username,
                fullName,
                email,
                subject = sub,
                scopes,
                claims,
                canRead = readCheck.Allowed,
                canWrite = writeCheck.Allowed
            };
        }

        return Ok(new
        {
            useOidcForWeb = cfg.UseOidcForWeb,
            useOidcForOData = cfg.UseOidcForOData,
            useAuthorizationServer = cfg.UseAuthorizationServer,
            oidcConfigured = isConfigured,
            authority = cfg.OidcAuthority,
            authzAudience = cfg.AuthzAudience,
            authzReadScope = cfg.AuthzReadScope,
            authzWriteScope = cfg.AuthzWriteScope,
            isAuthenticated,
            user = userObj,
            computedRedirectUri
        });
    }

    [HttpGet("login")]
    public async Task<IActionResult> Login([FromQuery] string? returnUrl = null)
    {
        try
        {
            var authUrl = await _authService.BuildAuthorizationUrlAsync(HttpContext, returnUrl);
            return Redirect(authUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate OIDC authorization redirect.");
            return Redirect(BuildSsoErrorUrl(ex.Message));
        }
    }

    [HttpGet("callback")]
    [HttpPost("callback")]
    [HttpGet("/signin-oidc")]
    [HttpPost("/signin-oidc")]
    public async Task<IActionResult> Callback()
    {
        string? error = null;
        string? errorDescription = null;
        string? code = null;
        string? state = null;

        if (HttpMethods.IsPost(Request.Method) && Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            error = form["error"].FirstOrDefault();
            errorDescription = form["error_description"].FirstOrDefault();
            code = form["code"].FirstOrDefault();
            state = form["state"].FirstOrDefault();
        }
        else
        {
            error = Request.Query["error"].FirstOrDefault();
            errorDescription = Request.Query["error_description"].FirstOrDefault();
            code = Request.Query["code"].FirstOrDefault();
            state = Request.Query["state"].FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            var msg = string.IsNullOrWhiteSpace(errorDescription) ? error : $"{error}: {errorDescription}";
            _logger.LogWarning("OIDC callback returned error from IdP: {Error}", msg);
            return Redirect(BuildSsoErrorUrl(msg));
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Redirect(BuildSsoErrorUrl("Missing authorization code or state in callback."));
        }

        try
        {
            var (principal, returnUrl) = await _authService.HandleCallbackAsync(HttpContext, code, state, HttpContext.RequestAborted);

            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            };

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);

            var targetUrl = ResolveReturnUrl(returnUrl);
            return Redirect(targetUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process OIDC callback and exchange tokens.");
            return Redirect(BuildSsoErrorUrl(ex.Message));
        }
    }

    [HttpGet("logout")]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var baseLanding = Request.PathBase.HasValue && !string.IsNullOrEmpty(Request.PathBase.Value)
            ? Request.PathBase.Value + "/"
            : "/";
        return Redirect(baseLanding);
    }

    private string BuildSsoErrorUrl(string message)
    {
        var baseLanding = Request.PathBase.HasValue && !string.IsNullOrEmpty(Request.PathBase.Value)
            ? Request.PathBase.Value + "/"
            : "/";

        return $"{baseLanding}?sso_error={Uri.EscapeDataString(message)}";
    }

    private string ResolveReturnUrl(string? returnUrl)
    {
        var baseLanding = Request.PathBase.HasValue && !string.IsNullOrEmpty(Request.PathBase.Value)
            ? Request.PathBase.Value + "/"
            : "/";

        if (string.IsNullOrWhiteSpace(returnUrl))
            return baseLanding;

        if (Url.IsLocalUrl(returnUrl) || returnUrl.StartsWith("/") || returnUrl.StartsWith(baseLanding))
            return returnUrl;

        return baseLanding;
    }
}
