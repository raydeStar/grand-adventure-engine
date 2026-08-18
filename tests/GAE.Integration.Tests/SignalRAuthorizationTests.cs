using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GAE.Integration.Tests;

/// <summary>Verifies real-time subscriptions enforce the same privilege boundary as REST endpoints.</summary>
public class SignalRAuthorizationTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;

    public SignalRAuthorizationTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminFeed_RejectsRegularUser_AndAcceptsAdmin()
    {
        await using var userConnection = await CreateConnectionAsync(
            GaeWebApplicationFactory.DefaultUserUsername,
            GaeWebApplicationFactory.DefaultUserPassword);
        await Assert.ThrowsAsync<HubException>(() => userConnection.InvokeAsync("JoinAdminFeed"));

        await using var adminConnection = await CreateConnectionAsync(
            GaeWebApplicationFactory.DefaultAdminUsername,
            GaeWebApplicationFactory.DefaultAdminPassword);
        await adminConnection.InvokeAsync("JoinAdminFeed");
    }

    private async Task<HubConnection> CreateConnectionAsync(string username, string password)
    {
        using var loginClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var loginResponse = await loginClient.PostAsJsonAsync("/api/dashboard/auth/login", new { username, password });
        loginResponse.EnsureSuccessStatusCode();
        var cookie = loginResponse.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(loginClient.BaseAddress!, "/hubs/game"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add("Cookie", cookie);
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }
}
