using System.Security.Claims;
using GAE.Dashboard.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GAE.Dashboard.Api.Controllers;

[ApiController]
[Route("api/dashboard/auth")]
public class DashboardAuthController : ControllerBase
{
    private readonly IDashboardAuthService _authService;

    public DashboardAuthController(IDashboardAuthService authService)
    {
        _authService = authService;
    }

    [AllowAnonymous]
    [HttpGet("options")]
    public IActionResult GetLoginOptions()
    {
        return Ok(new { accounts = _authService.GetLoginHints() });
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
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] DashboardLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "username and password are required." });

        var account = _authService.ValidateCredentials(request.Username, request.Password);
        if (account is null)
            return Unauthorized(new { error = "Invalid username or password." });

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
                IsPersistent = request.RememberMe,
                IssuedUtc = issuedAt,
                ExpiresUtc = issuedAt.AddHours(_authService.GetSessionLifetimeHours())
            });

        return Ok(_authService.CreateSessionDescriptor(principal));
    }

    [Authorize(Policy = DashboardPolicies.UserAccess)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { success = true });
    }
}

public class DashboardLoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}