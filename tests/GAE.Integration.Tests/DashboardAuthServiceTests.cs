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
