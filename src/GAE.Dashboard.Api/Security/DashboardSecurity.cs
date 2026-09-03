using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GAE.Engine.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GAE.Dashboard.Api.Security;

public static class DashboardRoles
{
    public const string User = "user";
    public const string Admin = "admin";
}

public static class DashboardPolicies
{
    public const string UserAccess = "DashboardUserAccess";
    public const string AdminAccess = "DashboardAdminAccess";
}

public static class DashboardClaimTypes
{
    public const string DisplayName = "gae.display_name";
}

public sealed class DashboardAuthOptions
{
    public const string SectionName = "DashboardAuth";

    public DashboardAccountOptions User { get; set; } = new()
    {
        Username = "user",
        Password = "GAE-User-Local!123",
        DisplayName = "User Workspace"
    };

    public DashboardAccountOptions Admin { get; set; } = new()
    {
        Username = "admin",
        Password = "GAE-Admin-Local!123",
        DisplayName = "Admin Console"
    };

    public int SessionHours { get; set; } = 12;

    public bool ShowLoginPasswords { get; set; }

    /// <summary>When false, the self-service registration endpoint is closed. Defaults to open.</summary>
    public bool AllowRegistration { get; set; } = true;
}

public sealed class DashboardAccountOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed record DashboardAccount(string Username, string Password, string Role, string DisplayName);

public sealed record DashboardLoginHint(string Username, string? Password, string Role, string DisplayName);

public sealed record DashboardSessionDescriptor(string Username, string Role, string DisplayName, bool IsAdmin);

public sealed record DashboardRegistrationResult(DashboardAccount? Account, string? Error, bool Conflict = false);

public interface IDashboardAuthService
{
    Task<DashboardAccount?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default);
    Task<DashboardRegistrationResult> RegisterAsync(string username, string password, string? displayName, CancellationToken ct = default);
    bool IsRegistrationOpen { get; }
    IReadOnlyList<DashboardLoginHint> GetLoginHints(bool includePasswords);
    DashboardSessionDescriptor CreateSessionDescriptor(ClaimsPrincipal principal);
    int GetSessionLifetimeHours();
    IReadOnlyList<string> GetStartupWarnings();
    IReadOnlyList<string> GetStartupErrors(bool isProduction);
}

/// <summary>PBKDF2-SHA256 password hashing for self-registered dashboard accounts.</summary>
public static class DashboardPasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations))
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class DashboardAuthService : IDashboardAuthService
{
    private static readonly DashboardAuthOptions DefaultOptions = new();
    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{2,31}$", RegexOptions.Compiled);

    // Stand-in compared against when the submitted username matches no account. Never a valid
    // credential; it only exists to keep the failure path's timing identical to the success path.
    private const string DecoyPassword = "gae-unknown-account-decoy-not-a-credential";
    private static readonly string DecoyHash = DashboardPasswordHasher.Hash(DecoyPassword);

    private readonly IOptionsMonitor<DashboardAuthOptions> _optionsMonitor;
    private readonly IDbContextFactory<GaeDbContext> _dbContextFactory;

    public DashboardAuthService(IOptionsMonitor<DashboardAuthOptions> optionsMonitor, IDbContextFactory<GaeDbContext> dbContextFactory)
    {
        _optionsMonitor = optionsMonitor;
        _dbContextFactory = dbContextFactory;
    }

    public bool IsRegistrationOpen => CurrentOptions.AllowRegistration;

    /// <summary>
    /// Validates a login against the two configuration-backed accounts first, then the
    /// self-registered accounts table. Both paths do constant work whether or not the username exists.
    /// </summary>
    public async Task<DashboardAccount?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        var trimmed = username?.Trim() ?? string.Empty;
        var configured = GetConfiguredAccounts().FirstOrDefault(candidate =>
            string.Equals(candidate.Username, trimmed, StringComparison.OrdinalIgnoreCase));

        if (configured is not null)
            return FixedTimeEquals(configured.Password, password) ? configured : null;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var normalized = Normalize(trimmed);
        var user = await db.DashboardUsers.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);

        // Verify against a decoy hash when the account is missing, so both paths cost the same.
        var matches = DashboardPasswordHasher.Verify(password ?? string.Empty, user?.PasswordHash ?? DecoyHash);
        if (user is null || !matches)
            return null;

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new DashboardAccount(user.Username, string.Empty, user.Role, user.DisplayName);
    }

    public async Task<DashboardRegistrationResult> RegisterAsync(string username, string password, string? displayName, CancellationToken ct = default)
    {
        if (!IsRegistrationOpen)
            return new DashboardRegistrationResult(null, "Registration is closed on this deployment.");

        var trimmed = username?.Trim() ?? string.Empty;
        if (!UsernamePattern.IsMatch(trimmed))
            return new DashboardRegistrationResult(null, "Username must be 3-32 characters: letters, digits, dot, underscore or hyphen.");

        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return new DashboardRegistrationResult(null, "Password must contain at least 8 characters.");

        if (password.Length > 256)
            return new DashboardRegistrationResult(null, "Password is too long.");

        if (GetConfiguredAccounts().Any(account => string.Equals(account.Username, trimmed, StringComparison.OrdinalIgnoreCase)))
            return new DashboardRegistrationResult(null, "That username is already taken.", Conflict: true);

        var normalized = Normalize(trimmed);
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        if (await db.DashboardUsers.AnyAsync(u => u.NormalizedUsername == normalized, ct))
            return new DashboardRegistrationResult(null, "That username is already taken.", Conflict: true);

        var entity = new DashboardUserEntity
        {
            Username = trimmed,
            NormalizedUsername = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? trimmed : displayName.Trim(),
            PasswordHash = DashboardPasswordHasher.Hash(password),
            Role = DashboardRoles.User
        };

        db.DashboardUsers.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique index on normalized_username lost a race with a concurrent registration.
            return new DashboardRegistrationResult(null, "That username is already taken.", Conflict: true);
        }

        return new DashboardRegistrationResult(new DashboardAccount(entity.Username, string.Empty, entity.Role, entity.DisplayName), null);
    }

    /// <summary>
    /// Ordinal equality that does not short-circuit on the first differing byte. Hashing both
    /// sides first keeps the comparison length-independent as well.
    /// </summary>
    private static bool FixedTimeEquals(string expected, string? candidate)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(expectedHash, candidateHash);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public IReadOnlyList<DashboardLoginHint> GetLoginHints(bool includePasswords)
    {
        return GetConfiguredAccounts()
            .Select(account => new DashboardLoginHint(account.Username, includePasswords ? account.Password : null, account.Role, account.DisplayName))
            .ToArray();
    }

    public DashboardSessionDescriptor CreateSessionDescriptor(ClaimsPrincipal principal)
    {
        var username = principal.Identity?.Name ?? string.Empty;
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? DashboardRoles.User;
        var displayName = principal.FindFirstValue(DashboardClaimTypes.DisplayName)
            ?? GetConfiguredAccounts().FirstOrDefault(account => string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? username;

        return new DashboardSessionDescriptor(username, role, displayName, string.Equals(role, DashboardRoles.Admin, StringComparison.OrdinalIgnoreCase));
    }

    public int GetSessionLifetimeHours()
    {
        return Math.Max(1, CurrentOptions.SessionHours);
    }

    public IReadOnlyList<string> GetStartupWarnings()
    {
        var warnings = new List<string>();
        if (string.Equals(CurrentOptions.User.Password, DefaultOptions.User.Password, StringComparison.Ordinal))
        {
            warnings.Add("Dashboard user account is using the local default password. Override DashboardAuth:User:Password for shared environments.");
        }

        if (string.Equals(CurrentOptions.Admin.Password, DefaultOptions.Admin.Password, StringComparison.Ordinal))
        {
            warnings.Add("Dashboard admin account is using the local default password. Override DashboardAuth:Admin:Password for shared environments.");
        }

        return warnings;
    }

    /// <summary>
    /// Rejects unsafe production credentials before the HTTP listener opens. Local development may
    /// retain the documented demo accounts, but a shared deployment must make an explicit choice.
    /// </summary>
    public IReadOnlyList<string> GetStartupErrors(bool isProduction)
    {
        if (!isProduction)
            return [];

        var options = CurrentOptions;
        var errors = new List<string>();
        ValidateProductionAccount(options.User, DefaultOptions.User, "user", errors);
        ValidateProductionAccount(options.Admin, DefaultOptions.Admin, "admin", errors);

        if (options.ShowLoginPasswords)
            errors.Add("DashboardAuth:ShowLoginPasswords must be false in Production.");

        if (string.Equals(options.User.Username?.Trim(), options.Admin.Username?.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("Dashboard user and admin usernames must be different in Production.");

        if (!string.IsNullOrEmpty(options.User.Password)
            && string.Equals(options.User.Password, options.Admin.Password, StringComparison.Ordinal))
        {
            errors.Add("Dashboard user and admin passwords must be different in Production.");
        }

        return errors;
    }

    private DashboardAuthOptions CurrentOptions => _optionsMonitor.CurrentValue;

    private IReadOnlyList<DashboardAccount> GetConfiguredAccounts()
    {
        var options = CurrentOptions;
        return
        [
            new DashboardAccount(NormalizeUsername(options.User.Username, DefaultOptions.User.Username), options.User.Password, DashboardRoles.User, string.IsNullOrWhiteSpace(options.User.DisplayName) ? DefaultOptions.User.DisplayName : options.User.DisplayName.Trim()),
            new DashboardAccount(NormalizeUsername(options.Admin.Username, DefaultOptions.Admin.Username), options.Admin.Password, DashboardRoles.Admin, string.IsNullOrWhiteSpace(options.Admin.DisplayName) ? DefaultOptions.Admin.DisplayName : options.Admin.DisplayName.Trim())
        ];
    }

    private static string NormalizeUsername(string configured, string fallback)
    {
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    private static void ValidateProductionAccount(
        DashboardAccountOptions configured,
        DashboardAccountOptions defaults,
        string accountName,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configured.Username))
            errors.Add($"Dashboard {accountName} username is required in Production.");

        if (string.IsNullOrWhiteSpace(configured.Password) || configured.Password.Length < 12)
            errors.Add($"Dashboard {accountName} password must contain at least 12 characters in Production.");
        else if (string.Equals(configured.Password, defaults.Password, StringComparison.Ordinal))
            errors.Add($"Dashboard {accountName} password still uses the local default. Set a unique production secret.");
    }
}

public static class DashboardSecurityExtensions
{
    public static bool IsDashboardApiOrHubPath(this PathString path)
    {
        return path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");
    }

    public static CookieAuthenticationEvents CreateCookieEvents()
    {
        return new CookieAuthenticationEvents
        {
            OnRedirectToLogin = (context) => WriteApiAuthResponseAsync(context, StatusCodes.Status401Unauthorized, "Authentication required."),
            OnRedirectToAccessDenied = (context) => WriteApiAuthResponseAsync(context, StatusCodes.Status403Forbidden, "You do not have permission to perform that action.")
        };
    }

    private static Task WriteApiAuthResponseAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode, string message)
    {
        if (!context.Request.Path.IsDashboardApiOrHubPath())
        {
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
