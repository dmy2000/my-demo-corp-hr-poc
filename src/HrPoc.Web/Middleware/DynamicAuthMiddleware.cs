using HrPoc.Web.Services;

namespace HrPoc.Web.Middleware;

public class DynamicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DynamicAuthMiddleware> _logger;

    public DynamicAuthMiddleware(RequestDelegate next, ILogger<DynamicAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, OidcAuthService authService)
    {
        var path = context.Request.Path;

        // 1. Static files / non-API HTML requests are always allowed
        if (IsStaticOrHtmlFile(path))
        {
            await _next(context);
            return;
        }

        // 2. Settings API is always accessible so administrators cannot be locked out
        if (path.StartsWithSegments("/api/settings"))
        {
            await _next(context);
            return;
        }

        // 3. Auth login/callback/logout routes are always accessible without Bearer parsing
        if (path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/auth/status") ||
            path.StartsWithSegments("/signin-oidc"))
        {
            await _next(context);
            return;
        }

        var cfg = await authService.GetConfigAsync(context.RequestAborted);

        // 4. Validate Bearer token if present in Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var isOData = path.StartsWithSegments("/odata");
                    var principal = await authService.ValidateBearerTokenAsync(token, isOData, context.RequestAborted);
                    if (principal != null)
                    {
                        context.User = principal;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Bearer token validation failed for {Path}: {Message}", path, ex.Message);

                    // If authentication is required for this route, immediately return 401 with details
                    bool isODataRoute = path.StartsWithSegments("/odata");
                    bool isApiRoute = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth/status");

                    if ((isODataRoute && cfg.UseOidcForOData) || (isApiRoute && cfg.UseOidcForWeb))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        var escapedError = ex.Message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                        context.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{escapedError}\"";
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "invalid_token",
                            message = $"Bearer token validation failed: {ex.Message}"
                        });
                        return;
                    }
                }
            }
        }

        if (path.StartsWithSegments("/api/auth/status"))
        {
            await _next(context);
            return;
        }

        // 5. Evaluate OData API requests (/odata/...)
        if (path.StartsWithSegments("/odata"))
        {
            if (!cfg.UseOidcForOData)
            {
                await _next(context);
                return;
            }

            if (context.User?.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "unauthorized",
                    message = "OIDC / OAuth 2.0 Bearer authentication is required for OData API endpoints."
                });
                return;
            }

            var (allowed, denyReason) = authService.CheckAccess(context.User, context.Request.Method, cfg, path.Value);
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "forbidden",
                    message = denyReason ?? "Access denied."
                });
                return;
            }

            await _next(context);
            return;
        }

        // 6. Evaluate Web REST API requests (/api/...)
        if (path.StartsWithSegments("/api"))
        {
            if (!cfg.UseOidcForWeb)
            {
                await _next(context);
                return;
            }

            if (context.User?.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "unauthorized",
                    ssoRequired = true,
                    message = "SSO authentication is required to access the HR Web UI."
                });
                return;
            }

            var (allowed, denyReason) = authService.CheckAccess(context.User, context.Request.Method, cfg, path.Value);
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "forbidden",
                    message = denyReason ?? "Access denied."
                });
                return;
            }

            await _next(context);
            return;
        }

        await _next(context);
    }

    private static bool IsStaticOrHtmlFile(PathString path)
    {
        if (!path.HasValue || path.Value == "/" || path.Value == string.Empty)
            return true;

        var val = path.Value;
        return val.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
               val.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
               val.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
               val.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
               val.StartsWith("/images/", StringComparison.OrdinalIgnoreCase) ||
               val.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase);
    }
}
