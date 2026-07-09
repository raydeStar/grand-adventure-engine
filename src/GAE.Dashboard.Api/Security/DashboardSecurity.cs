using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

public interface IDashboardAuthService
{
    DashboardAccount? ValidateCredentials(string username, string password);
    IReadOnlyList<DashboardLoginHint> GetLoginHints(bool includePasswords);
    DashboardSessionDescriptor CreateSessionDescriptor(ClaimsPrincipal principal);
    int GetSessionLifetimeHours();
    IReadOnlyList<string> GetStartupWarnings();
}

public sealed class DashboardAuthService : IDashboardAuthService
{
    private static readonly DashboardAuthOptions DefaultOptions = new();
    private readonly IOptionsMonitor<DashboardAuthOptions> _optionsMonitor;

    public DashboardAuthService(IOptionsMonitor<DashboardAuthOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public DashboardAccount? ValidateCredentials(string username, string password)
    {
        var account = GetAccounts().FirstOrDefault(candidate =>
            string.Equals(candidate.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));

        return account is not null && string.Equals(account.Password, password, StringComparison.Ordinal)
            ? account
            : null;
    }

    public IReadOnlyList<DashboardLoginHint> GetLoginHints(bool includePasswords)
    {
        return GetAccounts()
            .Select(account => new DashboardLoginHint(account.Username, includePasswords ? account.Password : null, account.Role, account.DisplayName))
            .ToArray();
    }

    public DashboardSessionDescriptor CreateSessionDescriptor(ClaimsPrincipal principal)
    {
        var username = principal.Identity?.Name ?? string.Empty;
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? DashboardRoles.User;
        var displayName = principal.FindFirstValue(DashboardClaimTypes.DisplayName)
            ?? GetAccounts().FirstOrDefault(account => string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase))?.DisplayName
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

    private DashboardAuthOptions CurrentOptions => _optionsMonitor.CurrentValue;

    private IReadOnlyList<DashboardAccount> GetAccounts()
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
