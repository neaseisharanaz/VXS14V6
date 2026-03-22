using Content.Server.Chat.Managers;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.GG.GameTicking.Rules.Components;
using Content.Shared.Chat;
using Content.Shared.GameTicking.Components;
using Content.Shared.GG.CapturePoint;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.GameTicking.Rules;

/// <summary>
/// This handles periodic ticket display in chat for capture point game modes.
/// </summary>
public sealed class TicketDisplaySystem : GameRuleSystem<TicketDisplayComponent>
{
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var currentTime = _timing.CurTime;

        // Query for entities that have all the required components
        var query = EntityQueryEnumerator<TicketDisplayComponent, CapturePointGameRuleComponent, GameRuleComponent, CaptureTicketsComponent>();

        while (query.MoveNext(out var uid, out var ticketDisplay, out var captureRule, out var gameRule, out var tickets))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            if (!ticketDisplay.DisplayEnabled)
                continue;

            // Check if it's time to display tickets
            if (currentTime - ticketDisplay.LastDisplayTime >= TimeSpan.FromSeconds(ticketDisplay.DisplayInterval))
            {
                DisplayTickets(captureRule, tickets);
                ticketDisplay.LastDisplayTime = currentTime;
            }
        }
    }

    /// <summary>
    /// Displays current ticket counts in chat
    /// </summary>
    private void DisplayTickets(CapturePointGameRuleComponent captureRule, CaptureTicketsComponent tickets)
    {
        var syndyTickets = tickets.SyndyTickets;
        var solfedTickets = tickets.SolfedTickets;

        // Create the ticket display message
        var message = Loc.GetString("ticket-display-message",
            ("syndyTickets", syndyTickets),
            ("solfedTickets", solfedTickets));

        var wrappedMessage = Loc.GetString("chat-manager-server-wrap-message", ("message", message));

        // Send to all players in the game
        _chatManager.DispatchServerAnnouncement(message);

        Log.Info($"[TicketDisplay] Tickets displayed: Syndy={syndyTickets}, Solfed={solfedTickets}");
    }
}
