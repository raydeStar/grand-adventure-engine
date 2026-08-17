using GAE.Dashboard.Api.Security;
using Microsoft.Extensions.Options;

namespace GAE.Integration.Tests;

public class DashboardAuthServiceTests
{
    [Fact]
    public void GetLoginHints_WhenPasswordsExcluded_LeavesPasswordsNull()
    {
        var service = new DashboardAuthService(new StaticOptionsMonitor<DashboardAuthOptions>(new DashboardAuthOptions()));

        var hints = service.GetLoginHints(includePasswords: false);

        Assert.NotEmpty(hints);
        Assert.All(hints, hint => Assert.Null(hint.Password));
        Assert.Contains(hints, hint => hint.Username == "user" && hint.Role == DashboardRoles.User);
        Assert.Contains(hints, hint => hint.Username == "admin" && hint.Role == DashboardRoles.Admin);
    }

    [Fact]
    public void GetLoginHints_WhenPasswordsIncluded_ReturnsConfiguredPasswords()
    {
        var service = new DashboardAuthService(new StaticOptionsMonitor<DashboardAuthOptions>(new DashboardAuthOptions()));

        var hints = service.GetLoginHints(includePasswords: true);

        Assert.Contains(hints, hint => hint.Username == "user" && hint.Password == "GAE-User-Local!123");
        Assert.Contains(hints, hint => hint.Username == "admin" && hint.Password == "GAE-Admin-Local!123");
    }

    [Fact]
    public void GetStartupErrors_InDevelopment_AllowsDocumentedLocalAccounts()
    {
        var service = new DashboardAuthService(new StaticOptionsMonitor<DashboardAuthOptions>(new DashboardAuthOptions()));

        var errors = service.GetStartupErrors(isProduction: false);

        Assert.Empty(errors);
    }

    [Fact]
    public void GetStartupErrors_InProduction_RejectsDefaultAccounts()
    {
        var service = new DashboardAuthService(new StaticOptionsMonitor<DashboardAuthOptions>(new DashboardAuthOptions()));

        var errors = service.GetStartupErrors(isProduction: true);

        Assert.Contains(errors, error => error.Contains("user password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("admin password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetStartupErrors_InProduction_AcceptsDistinctStrongAccounts()
    {
        var options = new DashboardAuthOptions
        {
            User = new DashboardAccountOptions
            {
                Username = "adventurer",
                Password = "A-Long-Unique-Player-Secret",
                DisplayName = "Player"
            },
            Admin = new DashboardAccountOptions
            {
                Username = "steward",
                Password = "A-Different-Admin-Secret",
                DisplayName = "Admin"
            }
        };
        var service = new DashboardAuthService(new StaticOptionsMonitor<DashboardAuthOptions>(options));

        var errors = service.GetStartupErrors(isProduction: true);

        Assert.Empty(errors);
    }

    [Fact]
    public void GetStartupErrors_InProduction_RejectsAnonymousPasswordHints()
    {
        var options = new DashboardAuthOptions
        {
            ShowLoginPasswords = true,
            User = new DashboardAccountOptions { Username = "player", Password = "A-Long-Unique-Player-Secret" },
            Admin = new DashboardAccountOptions { Username = "steward", Password = "A-Different-Admin-Secret" }
        };
        var service = new DashboardAuthService(new StaticOptionsMonitor<DashboardAuthOptions>(options));

        var errors = service.GetStartupErrors(isProduction: true);

        Assert.Contains(errors, error => error.Contains("ShowLoginPasswords", StringComparison.Ordinal));
    }
}

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value)
    {
        CurrentValue = value;
    }

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
