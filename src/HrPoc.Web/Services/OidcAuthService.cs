using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HrPoc.Web.Data;
using HrPoc.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace HrPoc.Web.Services;

public class OidcLoginState
{
    public string CodeVerifier { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class DiscoveryCacheEntry
{
    public OpenIdConnectConfiguration Configuration { get; set; } = null!;
    public ICollection<SecurityKey> SigningKeys { get; set; } = new List<SecurityKey>();
    public DateTime ExpiresAtUtc { get; set; }
    public string RawJson { get; set; } = string.Empty;
}

public class OidcDiscoveryResult
{
    public bool Success { get; set; }
    public string? Authority { get; set; }
    public string? Issuer { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? UserinfoEndpoint { get; set; }
    public string? JwksUri { get; set; }
    public int SigningKeysCount { get; set; }
    public List<string> ScopesSupported { get; set; } = new();
    public string? Error { get; set; }
}

public class OidcTokenTestResult
{
    public bool Success { get; set; }
    public string? TokenType { get; set; }
    public int? ExpiresIn { get; set; }
    public string? Scope { get; set; }
    public string? Audience { get; set; }
    public object? DecodedHeader { get; set; }
    public object? DecodedPayload { get; set; }
    public string? Error { get; set; }
    public string? RawResponse { get; set; }
}

public class OidcTokenInspectResult
{
    public bool Success { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? Subject { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public List<string> Scopes { get; set; } = new();
    public Dictionary<string, List<string>> Claims { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public string? ReadDenyReason { get; set; }
    public string? WriteDenyReason { get; set; }
    public string? Error { get; set; }
}

public class OidcAuthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcAuthService> _logger;

    private readonly SemaphoreSlim _configLock = new(1, 1);
    private InstallationConfig? _cachedConfig;
    private DateTime _configCacheExpiryUtc = DateTime.MinValue;

    private readonly ConcurrentDictionary<string, DiscoveryCacheEntry> _discoveryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OidcLoginState> _loginStates = new();

    public OidcAuthService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<OidcAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static string NormalizeAuthority(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return string.Empty;
        var trimmed = authority.Trim();
        if (trimmed.EndsWith("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/.well-known/openid-configuration".Length];
        }
        return trimmed.TrimEnd('/');
    }

    private static List<string> ParseConfiguredAudiences(string? configuredAudience)
    {
        if (string.IsNullOrWhiteSpace(configuredAudience))
            return new List<string>();

        return configuredAudience
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.TrimEnd('/'))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetPrimaryResourceUri(string? configuredAudience)
    {
        if (string.IsNullOrWhiteSpace(configuredAudience))
            return null;

        var first = configuredAudience
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }

    private static string? GetServiceBaseUri(string? configuredAudience)
    {
        var primary = GetPrimaryResourceUri(configuredAudience)?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(primary))
            return null;

        foreach (var suffix in new[] { "/odata/Employees", "/odata/Departments", "/odata/CostCenters", "/odata/Locations", "/odata", "/api/employees", "/api/departments", "/api/costcenters", "/api/locations", "/api" })
        {
            if (primary.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return primary[..^suffix.Length].TrimEnd('/');
            }
        }

        return primary;
    }

    private static (string? EntitySuffix, string? ProtocolSuffix) ResolveRequestResourceSuffixes(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            return (null, null);

        var p = requestPath.Trim();

        if (p.StartsWith("/odata", StringComparison.OrdinalIgnoreCase))
        {
            if (p.StartsWith("/odata/Employees", StringComparison.OrdinalIgnoreCase)) return ("/odata/Employees", "/odata");
            if (p.StartsWith("/odata/Departments", StringComparison.OrdinalIgnoreCase)) return ("/odata/Departments", "/odata");
            if (p.StartsWith("/odata/CostCenters", StringComparison.OrdinalIgnoreCase)) return ("/odata/CostCenters", "/odata");
            if (p.StartsWith("/odata/Locations", StringComparison.OrdinalIgnoreCase)) return ("/odata/Locations", "/odata");
            return (null, "/odata");
        }

        if (p.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            if (p.StartsWith("/api/employees", StringComparison.OrdinalIgnoreCase)) return ("/api/employees", "/api");
            if (p.StartsWith("/api/departments", StringComparison.OrdinalIgnoreCase)) return ("/api/departments", "/api");
            if (p.StartsWith("/api/costcenters", StringComparison.OrdinalIgnoreCase)) return ("/api/costcenters", "/api");
            if (p.StartsWith("/api/locations", StringComparison.OrdinalIgnoreCase)) return ("/api/locations", "/api");
            return (null, "/api");
        }

        return (null, null);
    }

    private static bool AudiencesMatch(IEnumerable<string> tokenAudiences, string? configuredAudience)
    {
        var expected = ParseConfiguredAudiences(configuredAudience);
        if (expected.Count == 0)
            return true;

        var baseRes = GetServiceBaseUri(configuredAudience);

        var normalizedTokenAuds = tokenAudiences
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().TrimEnd('/'))
            .ToList();

        return normalizedTokenAuds.Any(ta =>
            expected.Any(exp => string.Equals(ta, exp, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(baseRes) && (
                string.Equals(ta, baseRes, StringComparison.OrdinalIgnoreCase) ||
                ta.StartsWith(baseRes + "/odata", StringComparison.OrdinalIgnoreCase) ||
                ta.StartsWith(baseRes + "/api", StringComparison.OrdinalIgnoreCase))));
    }

    public void InvalidateCache()
    {
        _cachedConfig = null;
        _configCacheExpiryUtc = DateTime.MinValue;
        _discoveryCache.Clear();
    }

    public async Task<InstallationConfig> GetConfigAsync(CancellationToken ct = default)
    {
        if (_cachedConfig != null && DateTime.UtcNow < _configCacheExpiryUtc)
        {
            return _cachedConfig;
        }

        await _configLock.WaitAsync(ct);
        try
        {
            if (_cachedConfig != null && DateTime.UtcNow < _configCacheExpiryUtc)
            {
                return _cachedConfig;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();

            await InstallationConfigMigrator.EnsureSchemaAndDefaultRowAsync(db, ct);

            var config = await db.InstallationConfigs
                .AsNoTracking()
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync(ct);

            if (config == null)
            {
                config = new InstallationConfig
                {
                    IISSiteName = "Default Web Site",
                    IISAppSubPath = "hr",
                    InstallPath = @"C:\inetpub\wwwroot\hr",
                    AppPoolName = "HrPocAppPool",
                    AppSqlLogin = "hr_app",
                    AppUrl = "http://localhost/hr",
                    Version = "1.3.0",
                    OidcScopes = "openid profile email",
                    AuthzReadScope = "hr:read",
                    AuthzWriteScope = "hr:write",
                    InstalledAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                db.InstallationConfigs.Add(config);
                await db.SaveChangesAsync(ct);
            }

            _cachedConfig = config;
            _configCacheExpiryUtc = DateTime.UtcNow.AddSeconds(5);
            return _cachedConfig;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<InstallationConfig> UpdateConfigAsync(InstallationConfig updated, CancellationToken ct = default)
    {
        await _configLock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();

            await InstallationConfigMigrator.EnsureSchemaAndDefaultRowAsync(db, ct);

            var entity = await db.InstallationConfigs
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync(ct);

            if (entity == null)
            {
                entity = new InstallationConfig();
                db.InstallationConfigs.Add(entity);
            }

            entity.IISSiteName = updated.IISSiteName ?? entity.IISSiteName;
            entity.IISAppSubPath = updated.IISAppSubPath ?? entity.IISAppSubPath;
            entity.InstallPath = updated.InstallPath ?? entity.InstallPath;
            entity.AppPoolName = updated.AppPoolName ?? entity.AppPoolName;
            entity.AppSqlLogin = updated.AppSqlLogin ?? entity.AppSqlLogin;
            entity.AppUrl = updated.AppUrl;
            entity.Version = "1.3.0";
            entity.UpdatedAtUtc = DateTime.UtcNow;

            entity.UseOidcForWeb = updated.UseOidcForWeb;
            entity.UseOidcForOData = updated.UseOidcForOData;
            entity.UseAuthorizationServer = updated.UseAuthorizationServer;
            entity.OidcAuthority = updated.OidcAuthority?.Trim();
            entity.OidcClientId = updated.OidcClientId?.Trim();
            entity.OidcClientSecret = updated.OidcClientSecret?.Trim();
            entity.OidcRedirectUri = updated.OidcRedirectUri?.Trim();
            entity.OidcScopes = updated.OidcScopes?.Trim();
            entity.OidcRequireHttpsMetadata = updated.OidcRequireHttpsMetadata;
            entity.AuthzServerIssuer = updated.AuthzServerIssuer?.Trim();
            entity.AuthzAudience = updated.AuthzAudience?.Trim();
            entity.AuthzReadScope = updated.AuthzReadScope?.Trim();
            entity.AuthzWriteScope = updated.AuthzWriteScope?.Trim();
            entity.AuthzRequiredClaimType = updated.AuthzRequiredClaimType?.Trim();
            entity.AuthzRequiredClaimValue = updated.AuthzRequiredClaimValue?.Trim();

            await db.SaveChangesAsync(ct);

            _cachedConfig = entity;
            _configCacheExpiryUtc = DateTime.UtcNow.AddSeconds(5);
            _discoveryCache.Clear();

            return entity;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<DiscoveryCacheEntry> GetDiscoveryAsync(string authority, bool requireHttpsMetadata, CancellationToken ct = default)
    {
        var normalized = NormalizeAuthority(authority);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("OIDC Authority URL is required.", nameof(authority));
        }

        if (requireHttpsMetadata && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"HTTPS metadata is required, but authority URL is not HTTPS: '{normalized}'.");
        }

        if (_discoveryCache.TryGetValue(normalized, out var cached) && DateTime.UtcNow < cached.ExpiresAtUtc)
        {
            return cached;
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var discoveryUrl = $"{normalized}/.well-known/openid-configuration";
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(discoveryUrl, ct);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Failed to reach OpenID discovery endpoint at '{discoveryUrl}': {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Discovery endpoint '{discoveryUrl}' returned HTTP {response.StatusCode}: {err}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var config = OpenIdConnectConfiguration.Create(json);

        var signingKeys = new List<SecurityKey>();
        if (!string.IsNullOrWhiteSpace(config.JwksUri))
        {
            try
            {
                var jwksResp = await client.GetAsync(config.JwksUri, ct);
                if (jwksResp.IsSuccessStatusCode)
                {
                    var jwksJson = await jwksResp.Content.ReadAsStringAsync(ct);
                    var jwks = new JsonWebKeySet(jwksJson);
                    foreach (var key in jwks.GetSigningKeys())
                    {
                        signingKeys.Add(key);
                        config.SigningKeys.Add(key);
                    }
                }
                else
                {
                    _logger.LogWarning("JWKS endpoint '{JwksUri}' returned {StatusCode}", config.JwksUri, jwksResp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve signing keys from '{JwksUri}'", config.JwksUri);
            }
        }

        var entry = new DiscoveryCacheEntry
        {
            Configuration = config,
            SigningKeys = signingKeys,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            RawJson = json
        };

        _discoveryCache[normalized] = entry;
        return entry;
    }

    public async Task<string> BuildAuthorizationUrlAsync(HttpContext httpContext, string? returnUrl)
    {
        var cfg = await GetConfigAsync(httpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(cfg.OidcAuthority))
        {
            throw new InvalidOperationException("OIDC Authority URL is not configured. Please open Settings and configure your IdP.");
        }
        if (string.IsNullOrWhiteSpace(cfg.OidcClientId))
        {
            throw new InvalidOperationException("OIDC Client ID is not configured. Please open Settings and configure your IdP.");
        }

        var discovery = await GetDiscoveryAsync(cfg.OidcAuthority, cfg.OidcRequireHttpsMetadata, httpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(discovery.Configuration.AuthorizationEndpoint))
        {
            throw new InvalidOperationException("OIDC discovery document did not include an 'authorization_endpoint'.");
        }

        var redirectUri = !string.IsNullOrWhiteSpace(cfg.OidcRedirectUri)
            ? cfg.OidcRedirectUri.Trim()
            : $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}/api/auth/callback";

        var state = GenerateRandomBase64Url(32);
        var verifier = GenerateRandomBase64Url(32);
        var challenge = ComputeSha256Base64Url(verifier);
        var nonce = GenerateRandomBase64Url(16);

        // Clean up stale login states older than 15 minutes
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        foreach (var kvp in _loginStates)
        {
            if (kvp.Value.CreatedAtUtc < cutoff)
            {
                _loginStates.TryRemove(kvp.Key, out _);
            }
        }

        _loginStates[state] = new OidcLoginState
        {
            CodeVerifier = verifier,
            Nonce = nonce,
            ReturnUrl = returnUrl,
            CreatedAtUtc = DateTime.UtcNow
        };

        var scopes = !string.IsNullOrWhiteSpace(cfg.OidcScopes) ? cfg.OidcScopes.Trim() : "openid profile email";

        var query = new StringBuilder();
        query.Append(discovery.Configuration.AuthorizationEndpoint.Contains('?') ? "&" : "?");
        query.Append("client_id=").Append(Uri.EscapeDataString(cfg.OidcClientId.Trim()));
        query.Append("&response_type=code");
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        query.Append("&scope=").Append(Uri.EscapeDataString(scopes));
        query.Append("&state=").Append(Uri.EscapeDataString(state));
        query.Append("&nonce=").Append(Uri.EscapeDataString(nonce));
        query.Append("&code_challenge=").Append(Uri.EscapeDataString(challenge));
        query.Append("&code_challenge_method=S256");

        var primaryResource = GetPrimaryResourceUri(cfg.AuthzAudience);
        if (cfg.UseAuthorizationServer && !string.IsNullOrWhiteSpace(primaryResource))
        {
            query.Append("&resource=").Append(Uri.EscapeDataString(primaryResource));
        }

        return discovery.Configuration.AuthorizationEndpoint + query.ToString();
    }

    public async Task<(ClaimsPrincipal Principal, string? ReturnUrl)> HandleCallbackAsync(
        HttpContext httpContext,
        string code,
        string state,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state) || !_loginStates.TryRemove(state, out var loginState))
        {
            throw new InvalidOperationException("Invalid or expired login session state. Please initiate login again.");
        }

        var cfg = await GetConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(cfg.OidcAuthority))
        {
            throw new InvalidOperationException("OIDC Authority is not configured.");
        }

        var discovery = await GetDiscoveryAsync(cfg.OidcAuthority, cfg.OidcRequireHttpsMetadata, ct);
        if (string.IsNullOrWhiteSpace(discovery.Configuration.TokenEndpoint))
        {
            throw new InvalidOperationException("OIDC discovery document did not include a 'token_endpoint'.");
        }

        var redirectUri = !string.IsNullOrWhiteSpace(cfg.OidcRedirectUri)
            ? cfg.OidcRedirectUri.Trim()
            : $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}/api/auth/callback";

        var tokenParams = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = cfg.OidcClientId ?? string.Empty,
            ["code_verifier"] = loginState.CodeVerifier
        };

        if (!string.IsNullOrWhiteSpace(cfg.OidcClientSecret))
        {
            tokenParams["client_secret"] = cfg.OidcClientSecret.Trim();
        }

        var primaryResource = GetPrimaryResourceUri(cfg.AuthzAudience);
        if (cfg.UseAuthorizationServer && !string.IsNullOrWhiteSpace(primaryResource))
        {
            tokenParams["resource"] = primaryResource;
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, discovery.Configuration.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };

        var tokenResponse = await client.SendAsync(tokenRequest, ct);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Token exchange failed: HTTP {StatusCode} body: {Body}", tokenResponse.StatusCode, tokenBody);
            throw new InvalidOperationException($"Token exchange failed ({tokenResponse.StatusCode}): {tokenBody}");
        }

        using var jsonDoc = JsonDocument.Parse(tokenBody);
        var root = jsonDoc.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
        var idToken = root.TryGetProperty("id_token", out var itProp) ? itProp.GetString() : null;
        var tokenScope = root.TryGetProperty("scope", out var scProp) ? scProp.GetString() : null;

        var claims = new List<Claim>();

        // If ID token is present, decode and extract claims
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                handler.InboundClaimTypeMap.Clear();
                var parsedIdToken = handler.ReadJwtToken(idToken);
                foreach (var claim in parsedIdToken.Claims)
                {
                    if (!claims.Any(c => c.Type == claim.Type && c.Value == claim.Value))
                    {
                        claims.Add(new Claim(claim.Type, claim.Value));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse id_token claims.");
            }
        }

        // If access token is a JWT, extract claims
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                handler.InboundClaimTypeMap.Clear();
                if (handler.CanReadToken(accessToken))
                {
                    var parsedAt = handler.ReadJwtToken(accessToken);
                    foreach (var claim in parsedAt.Claims)
                    {
                        if (!claims.Any(c => c.Type == claim.Type && c.Value == claim.Value))
                        {
                            claims.Add(new Claim(claim.Type, claim.Value));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Access token is not a readable JWT.");
            }
        }

        // Call Userinfo endpoint if available to enrich claims
        if (!string.IsNullOrWhiteSpace(discovery.Configuration.UserInfoEndpoint) && !string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                var userinfoReq = new HttpRequestMessage(HttpMethod.Get, discovery.Configuration.UserInfoEndpoint);
                userinfoReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var userinfoResp = await client.SendAsync(userinfoReq, ct);
                if (userinfoResp.IsSuccessStatusCode)
                {
                    var userinfoJson = await userinfoResp.Content.ReadAsStringAsync(ct);
                    using var userInfoDoc = JsonDocument.Parse(userinfoJson);
                    foreach (var prop in userInfoDoc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var val = prop.Value.GetString();
                            if (!string.IsNullOrEmpty(val) && !claims.Any(c => c.Type == prop.Name && c.Value == val))
                            {
                                claims.Add(new Claim(prop.Name, val));
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                var val = item.ToString();
                                if (!string.IsNullOrEmpty(val) && !claims.Any(c => c.Type == prop.Name && c.Value == val))
                                {
                                    claims.Add(new Claim(prop.Name, val));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Userinfo enrichment failed.");
            }
        }

        // Normalize scopes
        if (!string.IsNullOrWhiteSpace(tokenScope))
        {
            foreach (var scopePart in tokenScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!claims.Any(c => (c.Type == "scope" || c.Type == "scp") && c.Value == scopePart))
                {
                    claims.Add(new Claim("scope", scopePart));
                    claims.Add(new Claim("scp", scopePart));
                }
            }
        }

        // Expand any concatenated "scope" claims
        var existingScopeClaims = claims.Where(c => c.Type == "scope" || c.Type == "scp").ToList();
        foreach (var sc in existingScopeClaims)
        {
            if (sc.Value.Contains(' '))
            {
                foreach (var part in sc.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!claims.Any(c => (c.Type == "scope" || c.Type == "scp") && c.Value == part))
                    {
                        claims.Add(new Claim("scope", part));
                        claims.Add(new Claim("scp", part));
                    }
                }
            }
        }

        // Determine Identity Name and Identifier
        var username = claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                    ?? claims.FirstOrDefault(c => c.Type == "email")?.Value
                    ?? claims.FirstOrDefault(c => c.Type == "name")?.Value
                    ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                    ?? "AuthenticatedUser";

        var fullName = claims.FirstOrDefault(c => c.Type == "name")?.Value
                    ?? claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                    ?? username;

        var email = claims.FirstOrDefault(c => c.Type == "email")?.Value;
        var sub = claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? username;

        if (!claims.Any(c => c.Type == ClaimTypes.Name))
            claims.Add(new Claim(ClaimTypes.Name, username));
        if (!claims.Any(c => c.Type == ClaimTypes.NameIdentifier))
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
        if (!string.IsNullOrEmpty(email) && !claims.Any(c => c.Type == ClaimTypes.Email))
            claims.Add(new Claim(ClaimTypes.Email, email));

        var identity = new ClaimsIdentity(claims, "HrPocCookieAuth", ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        return (principal, loginState.ReturnUrl);
    }

    public async Task<ClaimsPrincipal> ValidateBearerTokenAsync(string token, bool isODataRequest, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Bearer token is empty.", nameof(token));
        }

        var cfg = await GetConfigAsync(ct);

        var authorityToUse = !string.IsNullOrWhiteSpace(cfg.AuthzServerIssuer)
            ? cfg.AuthzServerIssuer.Trim()
            : cfg.OidcAuthority?.Trim();

        if (string.IsNullOrWhiteSpace(authorityToUse))
        {
            throw new InvalidOperationException("Neither OIDC Authority nor Authorization Server Issuer is configured in Settings.");
        }

        var normalizedAuthority = NormalizeAuthority(authorityToUse);
        var discovery = await GetDiscoveryAsync(normalizedAuthority, cfg.OidcRequireHttpsMetadata, ct);

        if (discovery.SigningKeys.Count == 0)
        {
            throw new SecurityTokenValidationException($"No signing keys were retrieved from '{normalizedAuthority}/.well-known/openid-configuration'.");
        }

        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();

        var validIssuers = new List<string>
        {
            normalizedAuthority,
            normalizedAuthority + "/"
        };

        if (discovery.Configuration.Issuer != null && !validIssuers.Contains(discovery.Configuration.Issuer))
        {
            validIssuers.Add(discovery.Configuration.Issuer);
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = false, // audience is evaluated below for detailed error reporting
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        SecurityToken validatedSecurityToken;
        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(token, validationParameters, out validatedSecurityToken);
        }
        catch (Exception ex)
        {
            throw new SecurityTokenValidationException($"JWT token validation failed: {ex.Message}", ex);
        }

        var jwt = (JwtSecurityToken)validatedSecurityToken;

        // If UseAuthorizationServer is active and AuthzAudience is configured, validate audience
        if (cfg.UseAuthorizationServer && !string.IsNullOrWhiteSpace(cfg.AuthzAudience))
        {
            var audiences = jwt.Audiences?.ToList() ?? new List<string>();
            if (audiences.Count == 0)
            {
                audiences = principal.FindAll("aud").Select(c => c.Value).ToList();
            }

            if (!AudiencesMatch(audiences, cfg.AuthzAudience))
            {
                var audList = audiences.Count > 0 ? string.Join(", ", audiences) : "none";
                throw new SecurityTokenValidationException(
                    $"Token audience '[{audList}]' does not match the configured API Authorization Server audience '{cfg.AuthzAudience.Trim()}'.");
            }
        }

        // Expand scope / scp claims
        if (principal.Identity is ClaimsIdentity identity)
        {
            var scopeClaims = principal.FindAll("scope").Concat(principal.FindAll("scp")).ToList();
            foreach (var sc in scopeClaims)
            {
                if (sc.Value.Contains(' '))
                {
                    foreach (var part in sc.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!principal.HasClaim(c => (c.Type == "scope" || c.Type == "scp") && c.Value == part))
                        {
                            identity.AddClaim(new Claim("scope", part));
                            identity.AddClaim(new Claim("scp", part));
                        }
                    }
                }
            }

            var username = principal.FindFirst("preferred_username")?.Value
                        ?? principal.FindFirst("email")?.Value
                        ?? principal.FindFirst("name")?.Value
                        ?? principal.FindFirst("sub")?.Value
                        ?? principal.FindFirst("client_id")?.Value
                        ?? "BearerUser";

            if (!principal.HasClaim(c => c.Type == ClaimTypes.Name))
            {
                identity.AddClaim(new Claim(ClaimTypes.Name, username));
            }
        }

        return principal;
    }

    public (bool Allowed, string? DenyReason) CheckAccess(ClaimsPrincipal user, string httpMethod, InstallationConfig cfg, string? requestPath = null)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return (false, "User is not authenticated.");
        }

        // When Authorization Server enforcement is disabled, allow any operation to any authenticated user/token
        if (!cfg.UseAuthorizationServer)
        {
            return (true, null);
        }

        // 1. Audience & Entity Resource verification (if token claims contain aud)
        if (!string.IsNullOrWhiteSpace(cfg.AuthzAudience))
        {
            var audClaims = user.FindAll("aud").Select(c => c.Value.Trim().TrimEnd('/')).Where(v => !string.IsNullOrEmpty(v)).ToList();
            bool isIdTokenOnlyAudience = !string.IsNullOrWhiteSpace(cfg.OidcClientId) &&
                                         audClaims.Count > 0 &&
                                         audClaims.All(a => string.Equals(a, cfg.OidcClientId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (audClaims.Count > 0 && !isIdTokenOnlyAudience)
            {
                if (!AudiencesMatch(audClaims, cfg.AuthzAudience))
                {
                    return (false, $"Token audience '[{string.Join(", ", audClaims)}]' does not match required Authorization Server audience '{cfg.AuthzAudience.Trim()}'.");
                }

                var baseRes = GetServiceBaseUri(cfg.AuthzAudience) ?? string.Empty;
                var configuredList = ParseConfiguredAudiences(cfg.AuthzAudience);
                var (entitySuffix, protocolSuffix) = ResolveRequestResourceSuffixes(requestPath);

                if (!string.IsNullOrEmpty(entitySuffix))
                {
                    var expectedEntityAud = baseRes + entitySuffix;
                    var expectedProtocolAud = baseRes + protocolSuffix;

                    // If admin explicitly restricted AuthzAudience in Settings to specific entity paths, enforce that configured list
                    bool hasExplicitSubpaths = configuredList.Any(c =>
                        c.Contains("/odata/", StringComparison.OrdinalIgnoreCase) ||
                        c.Contains("/api/", StringComparison.OrdinalIgnoreCase));

                    if (hasExplicitSubpaths)
                    {
                        bool configuredAllowsEntity = configuredList.Any(c =>
                            string.Equals(c, expectedEntityAud, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c, expectedProtocolAud, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c, baseRes, StringComparison.OrdinalIgnoreCase) ||
                            c.EndsWith(entitySuffix, StringComparison.OrdinalIgnoreCase));

                        if (!configuredAllowsEntity)
                        {
                            return (false, $"Authorization Server settings do not permit access to resource '{expectedEntityAud}'.");
                        }
                    }

                    // Verify token's aud claim covers this specific entity, its protocol root, or the base resource URI
                    bool tokenCoversEntity = audClaims.Any(ta =>
                        string.Equals(ta, expectedEntityAud, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ta, expectedProtocolAud, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(baseRes) && string.Equals(ta, baseRes, StringComparison.OrdinalIgnoreCase)) ||
                        ta.EndsWith(entitySuffix, StringComparison.OrdinalIgnoreCase));

                    if (!tokenCoversEntity)
                    {
                        return (false, $"Token audience '[{string.Join(", ", audClaims)}]' does not grant access to resource '{expectedEntityAud}'.");
                    }
                }
            }
        }

        // 2. Custom Claim verification (if both type and value configured)
        if (!string.IsNullOrWhiteSpace(cfg.AuthzRequiredClaimType) && !string.IsNullOrWhiteSpace(cfg.AuthzRequiredClaimValue))
        {
            var claimType = cfg.AuthzRequiredClaimType.Trim();
            var requiredVal = cfg.AuthzRequiredClaimValue.Trim();

            var userClaimValues = user.Claims
                .Where(c => string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value)
                .ToList();

            bool matched = false;
            foreach (var val in userClaimValues)
            {
                if (string.Equals(val, requiredVal, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
                var parts = val.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Any(p => string.Equals(p, requiredVal, StringComparison.OrdinalIgnoreCase)))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                var actualStr = userClaimValues.Count > 0 ? string.Join(", ", userClaimValues) : "none";
                return (false, $"Missing required claim '{claimType}' with value '{requiredVal}'. User claim values: [{actualStr}].");
            }
        }

        // 3. Scope verification based on HTTP method
        var userScopes = ExtractAllScopes(user);
        var method = (httpMethod ?? "GET").ToUpperInvariant();
        bool isRead = method == "GET" || method == "HEAD" || method == "OPTIONS";

        if (isRead)
        {
            var readScopes = SplitScopes(cfg.AuthzReadScope);
            var writeScopes = SplitScopes(cfg.AuthzWriteScope);

            if (readScopes.Count > 0)
            {
                bool hasRead = readScopes.Any(s => userScopes.Contains(s, StringComparer.OrdinalIgnoreCase));
                bool hasWrite = writeScopes.Any(s => userScopes.Contains(s, StringComparer.OrdinalIgnoreCase));

                if (!hasRead && !hasWrite)
                {
                    var grantedStr = userScopes.Count > 0 ? string.Join(" ", userScopes) : "none";
                    var reqStr = string.Join(" or ", readScopes);
                    if (writeScopes.Count > 0) reqStr += $" (or {string.Join(" or ", writeScopes)})";
                    return (false, $"Insufficient scope for read operation ({method}). Required: {reqStr}. Granted scopes: [{grantedStr}].");
                }
            }
        }
        else // Write / Mutating methods: POST, PUT, PATCH, DELETE
        {
            var writeScopes = SplitScopes(cfg.AuthzWriteScope);
            if (writeScopes.Count > 0)
            {
                bool hasWrite = writeScopes.Any(s => userScopes.Contains(s, StringComparer.OrdinalIgnoreCase));
                if (!hasWrite)
                {
                    var grantedStr = userScopes.Count > 0 ? string.Join(" ", userScopes) : "none";
                    return (false, $"Insufficient scope for write operation ({method}). Required scope: '{cfg.AuthzWriteScope}'. Granted scopes: [{grantedStr}].");
                }
            }
        }

        return (true, null);
    }

    public List<string> ExtractAllScopes(ClaimsPrincipal user)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in user.Claims)
        {
            if (claim.Type == "scope" || claim.Type == "scp" ||
                claim.Type == "http://schemas.microsoft.com/identity/claims/scope")
            {
                foreach (var part in claim.Value.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    result.Add(part);
                }
            }
        }
        return result.ToList();
    }

    private static List<string> SplitScopes(string? scopeConfig)
    {
        if (string.IsNullOrWhiteSpace(scopeConfig)) return new List<string>();
        return scopeConfig.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public async Task<OidcDiscoveryResult> TestDiscoveryAsync(string? authority, bool requireHttpsMetadata, CancellationToken ct = default)
    {
        var result = new OidcDiscoveryResult();
        try
        {
            var normalized = NormalizeAuthority(authority);
            result.Authority = normalized;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                result.Success = false;
                result.Error = "Authority URL cannot be empty.";
                return result;
            }

            var entry = await GetDiscoveryAsync(normalized, requireHttpsMetadata, ct);
            result.Success = true;
            result.Issuer = entry.Configuration.Issuer;
            result.AuthorizationEndpoint = entry.Configuration.AuthorizationEndpoint;
            result.TokenEndpoint = entry.Configuration.TokenEndpoint;
            result.UserinfoEndpoint = entry.Configuration.UserInfoEndpoint;
            result.JwksUri = entry.Configuration.JwksUri;
            result.SigningKeysCount = entry.SigningKeys.Count;
            result.ScopesSupported = entry.Configuration.ScopesSupported?.ToList() ?? new List<string>();
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }

    public async Task<OidcTokenTestResult> TestTokenAsync(
        string? authority,
        string? clientId,
        string? clientSecret,
        string? audience,
        string? scopes,
        CancellationToken ct = default)
    {
        var result = new OidcTokenTestResult();
        try
        {
            var normalized = NormalizeAuthority(authority);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                result.Success = false;
                result.Error = "Authority URL is required.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(clientId))
            {
                result.Success = false;
                result.Error = "Client ID is required.";
                return result;
            }

            var entry = await GetDiscoveryAsync(normalized, false, ct);
            if (string.IsNullOrWhiteSpace(entry.Configuration.TokenEndpoint))
            {
                result.Success = false;
                result.Error = "Discovery metadata does not contain a 'token_endpoint'.";
                return result;
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var postParams = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId.Trim()
            };

            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                postParams["client_secret"] = clientSecret.Trim();
            }

            if (!string.IsNullOrWhiteSpace(scopes))
            {
                postParams["scope"] = scopes.Trim();
            }

            var primaryResource = GetPrimaryResourceUri(audience);
            if (!string.IsNullOrWhiteSpace(primaryResource))
            {
                postParams["resource"] = primaryResource;
            }

            var req = new HttpRequestMessage(HttpMethod.Post, entry.Configuration.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(postParams)
            };

            var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            result.RawResponse = body;

            if (!resp.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Error = $"Token endpoint returned HTTP {resp.StatusCode}: {body}";
                return result;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            result.TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : null;
            result.ExpiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : null;
            result.Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var handler = new JwtSecurityTokenHandler();
                handler.InboundClaimTypeMap.Clear();
                if (handler.CanReadToken(accessToken))
                {
                    var jwt = handler.ReadJwtToken(accessToken);
                    result.Audience = string.Join(", ", jwt.Audiences);
                    result.DecodedHeader = jwt.Header;
                    result.DecodedPayload = jwt.Payload;
                }
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }

    public async Task<OidcTokenInspectResult> InspectTokenAsync(string token, CancellationToken ct = default)
    {
        var result = new OidcTokenInspectResult();
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                result.Success = false;
                result.Error = "Token is empty.";
                return result;
            }

            var cfg = await GetConfigAsync(ct);
            var principal = await ValidateBearerTokenAsync(token, isODataRequest: false, ct);

            result.Success = true;
            result.Subject = principal.FindFirst("sub")?.Value ?? principal.Identity?.Name;
            result.Issuer = principal.FindFirst("iss")?.Value;
            result.Audience = string.Join(", ", principal.FindAll("aud").Select(c => c.Value));

            var expClaim = principal.FindFirst("exp")?.Value;
            if (long.TryParse(expClaim, out var expSeconds))
            {
                result.ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
            }

            result.Scopes = ExtractAllScopes(principal);

            foreach (var claim in principal.Claims)
            {
                if (!result.Claims.TryGetValue(claim.Type, out var list))
                {
                    list = new List<string>();
                    result.Claims[claim.Type] = list;
                }
                list.Add(claim.Value);
            }

            var readCheck = CheckAccess(principal, "GET", cfg);
            result.CanRead = readCheck.Allowed;
            result.ReadDenyReason = readCheck.DenyReason;

            var writeCheck = CheckAccess(principal, "POST", cfg);
            result.CanWrite = writeCheck.Allowed;
            result.WriteDenyReason = writeCheck.DenyReason;

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }

    private static string GenerateRandomBase64Url(int byteCount = 32)
    {
        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ComputeSha256Base64Url(string input)
    {
        var bytes = Encoding.ASCII.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }
}
