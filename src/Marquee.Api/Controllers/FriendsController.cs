using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/friends")]
[Authorize]
public class FriendsController(IFriendshipService friendships) : ControllerBase
{
    /// <summary>The signed-in user's accepted friends.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FriendDto>>> List(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        return Ok(await friendships.ListFriendsAsync(userId.Value, ct));
    }

    /// <summary>Pending requests in both directions, flagged with which way each one points.</summary>
    [HttpGet("requests")]
    public async Task<ActionResult<IReadOnlyList<FriendRequestDto>>> Requests(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        return Ok(await friendships.ListRequestsAsync(userId.Value, ct));
    }

    [HttpPost("requests")]
    public async Task<ActionResult<FriendRequestDto>> Send(SendFriendRequestRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        return Respond(await friendships.SendRequestAsync(userId.Value, request.Username, ct));
    }

    [HttpPost("requests/{id:guid}/accept")]
    public async Task<ActionResult<FriendRequestDto>> Accept(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        return Respond(await friendships.AcceptAsync(userId.Value, id, ct));
    }

    [HttpPost("requests/{id:guid}/reject")]
    public async Task<ActionResult<FriendRequestDto>> Reject(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        return Respond(await friendships.RejectAsync(userId.Value, id, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var result = await friendships.RemoveFriendAsync(userId.Value, id, ct);
        return result.Outcome == FriendActionOutcome.Ok ? NoContent() : NotFound();
    }

    private ActionResult<FriendRequestDto> Respond(FriendActionResult result) => result.Outcome switch
    {
        FriendActionOutcome.UserNotFound => NotFound(new { error = "No such user." }),
        FriendActionOutcome.RequestNotFound => NotFound(new { error = "No such friend request." }),
        FriendActionOutcome.Self => BadRequest(new { error = "You cannot send yourself a friend request." }),
        FriendActionOutcome.AlreadyFriends => Conflict(new { error = "You are already friends." }),
        FriendActionOutcome.AlreadyPending => Conflict(new { error = "That request is already pending." }),
        // Deliberately 404, not 403: telling a caller "that request exists but is not yours" would
        // confirm the existence of a request between two other people.
        FriendActionOutcome.NotAllowed => NotFound(new { error = "No such friend request." }),
        _ => Ok(result.Request)
    };
}
