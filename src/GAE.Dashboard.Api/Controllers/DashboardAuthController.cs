using System.Security.Claims;
using GAE.Dashboard.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GAE.Dashboard.Api.Controllers;

[ApiController]
[Route("api/dashboard/auth")]
public class DashboardAuthController : ControllerBase
{
    private readonly IDashboardAuthService _authService;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public DashboardAuthController(
        IDashboardAuthService authService,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _authService = authService;
        _environment = environment;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpGet("options")]
    public IActionResult GetLoginOptions()
    {
        var includePasswords = !_environment.IsProduction()
            || _configuration.GetValue<bool>("DashboardAuth:ShowLoginPasswords");

        return Ok(new
        {
            accounts = _authService.GetLoginHints(includePasswords),
            registrationOpen = _authService.IsRegistrationOpen
        });
    }

    [AllowAnonymous]
    [HttpGet("session")]
    public IActionResult GetSession()
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
            return Content("null", "application/json");

        return Ok(_authService.CreateSessionDescriptor(User));
    }

    [AllowAnonymous]
    [EnableRateLimiting("dashboard-login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] DashboardLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "username and password are required." });

        var account = await _authService.ValidateCredentialsAsync(request.Username, request.Password, ct);
        if (account is null)
            return Unauthorized(new { error = "Invalid username or password." });

        var principal = await SignInAsync(account, request.RememberMe);
        return Ok(_authService.CreateSessionDescriptor(principal));
    }

    /// <summary>Creates a self-service account and signs it in. Every character it creates belongs to it alone.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("dashboard-login")]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] DashboardRegisterRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "username and password are required." });

        var result = await _authService.RegisterAsync(request.Username, request.Password, request.DisplayName, ct);
        if (result.Account is null)
        {
            var payload = new { error = result.Error ?? "Registration failed." };
            return result.Conflict ? Conflict(payload) : BadRequest(payload);
        }

        var principal = await SignInAsync(result.Account, request.RememberMe);
        return Ok(_authService.CreateSessionDescriptor(principal));
    }

    [Authorize(Policy = DashboardPolicies.UserAccess)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { success = true });
    }

    private async Task<ClaimsPrincipal> SignInAsync(DashboardAccount account, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Username),
            new(ClaimTypes.Name, account.Username),
            new(ClaimTypes.Role, account.Role),
            new(DashboardClaimTypes.DisplayName, account.DisplayName)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var issuedAt = DateTimeOffset.UtcNow;

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                IssuedUtc = issuedAt,
                ExpiresUtc = issuedAt.AddHours(_authService.GetSessionLifetimeHours())
            });

        return principal;
    }
}

public class DashboardLoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public class DashboardRegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool RememberMe { get; set; }
}
