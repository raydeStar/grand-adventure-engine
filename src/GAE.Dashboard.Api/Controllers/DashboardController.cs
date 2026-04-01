using GAE.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace GAE.Dashboard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IStateManager _stateManager;
    private readonly IGameEngine _engine;

    public DashboardController(IStateManager stateManager, IGameEngine engine)
    {
        _stateManager = stateManager;
        _engine = engine;
    }

    [HttpGet("players")]
    public async Task<IActionResult> GetPlayers(CancellationToken ct)
    {
        var players = await _stateManager.GetAllPlayersAsync(ct);
        return Ok(players);
    }

    [HttpGet("players/{playerId}")]
    public async Task<IActionResult> GetPlayer(string playerId, CancellationToken ct)
    {
        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        return player is not null ? Ok(player) : NotFound();
    }

    [HttpGet("rooms")]
    public async Task<IActionResult> GetRooms(CancellationToken ct)
    {
        var rooms = await _stateManager.GetAllRoomsAsync(ct);
        return Ok(rooms);
    }

    [HttpGet("rooms/{roomId}")]
    public async Task<IActionResult> GetRoom(string roomId, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(roomId, ct);
        return room is not null ? Ok(room) : NotFound();
    }

    [HttpGet("story")]
    public async Task<IActionResult> GetStory([FromQuery] string? playerId = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var entries = await _stateManager.GetStoryEntriesAsync(playerId, limit, ct);
        return Ok(entries);
    }

    [HttpGet("story/room/{roomId}")]
    public async Task<IActionResult> GetRoomStory(string roomId, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var entries = await _stateManager.GetRecentStoryForRoomAsync(roomId, limit, ct);
        return Ok(entries);
    }

    [HttpPost("action")]
    public async Task<IActionResult> ProcessAction([FromBody] ActionRequest request, CancellationToken ct)
    {
        var action = _engine.ParseCommand(request.PlayerId, request.Command);
        var result = await _engine.ProcessActionAsync(request.PlayerId, action, ct);
        return Ok(result);
    }
}

public class ActionRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}
