using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Mind;
using Content.Server.RoundEnd;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.GG.CapturePoint;
using Content.Shared.Mobs;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.VXS14.TicketBattle;

/// <summary>
/// Manages the Ticket Battle game mode.
/// Both teams start with <see cref="TicketBattleGameRuleComponent.InitialTickets"/> tickets.
/// On each player death its role's <see cref="RoleTicketCostComponent.TicketCost"/> is deducted
/// from the owning team's pool.  The first team to hit 0 loses.
/// </summary>
public sealed class TicketBattleRuleSystem : GameRuleSystem<TicketBattleGameRuleComponent>
{
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly RespawnRuleSystem _respawn = default!;
    [Dependency] private readonly RoundEndSystem _roundEnd = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawnComplete);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobDeath);
    }

    protected override void Started(
        EntityUid uid,
        TicketBattleGameRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        component.SolfedTickets = component.InitialTickets;
        component.SyndyTickets = component.InitialTickets;
        SyncTicketsComponent(uid, component);
    }

    // ------------------------------------------------------------------ //
    // Spawning
    // ------------------------------------------------------------------ //

    private void OnBeforeSpawn(PlayerBeforeSpawnEvent ev)
    {
        var query = EntityQueryEnumerator<TicketBattleGameRuleComponent, RespawnTrackerComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var rule, out var tracker, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            if (ev.JobId == null)
                continue;

            // Only handle first-time spawn; subsequent respawns reuse the stored job.
            if (!rule.PlayerFirstJob.ContainsKey(ev.Player))
            {
                rule.PlayerFirstJob[ev.Player] = ev.JobId;
                Log.Info($"[TicketBattle] {ev.Player.Name} chose job {ev.JobId}");
            }

            var job = rule.PlayerFirstJob[ev.Player];
            var team = ResolveTeam(job);

            if (team == null)
            {
                Log.Error($"[TicketBattle] Unknown team for job '{job}' – skipping.");
                continue;
            }

            // Find the team spawn point.
            var spawnCoords = FindSpawnPoint(team);
            if (!spawnCoords.IsValid(EntityManager))
            {
                Log.Warning($"[TicketBattle] Falling back to station coordinates for {ev.Player.Name} ({team}).");
                spawnCoords = Transform(ev.Station).Coordinates;
            }

            // Create mind.
            var mind = _mind.CreateMind(ev.Player.UserId, ev.Profile.Name);
            _mind.SetUserId(mind, ev.Player.UserId);

            // Spawn mob at the team's spawn location (falls back to station spawn if no point found).
            var mob = _stationSpawning.SpawnPlayerMob(spawnCoords, job, ev.Profile, ev.Station);

            DebugTools.AssertNotNull(mob);

            _mind.TransferTo(mind, mob);

            // Attach team component so death handlers can identify the team.
            if (team == "Solfed")
                EnsureComp<GGSolfedTeamComponent>(mob);
            else
                EnsureComp<GGSyndyTeamComponent>(mob);

            _respawn.AddToTracker(ev.Player.UserId, (uid, tracker));

            ev.Handled = true;
            Log.Info($"[TicketBattle] Spawned {ev.Player.Name} as {team} at spawn point.");
            break;
        }
    }

    private void OnSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        var query = EntityQueryEnumerator<TicketBattleGameRuleComponent, RespawnTrackerComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out _, out var tracker, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;
            _respawn.AddToTracker((ev.Mob, null), (uid, tracker));
        }
    }

    // ------------------------------------------------------------------ //
    // Death handlers
    // ------------------------------------------------------------------ //

    private void OnMobDeath(MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        var isSolfed = HasComp<GGSolfedTeamComponent>(args.Target);
        var isSyndy  = HasComp<GGSyndyTeamComponent>(args.Target);

        if (!isSolfed && !isSyndy)
            return;

        var cost = GetTicketCost(args.Target);

        var query = EntityQueryEnumerator<TicketBattleGameRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var rule, out var gameRule))
        {
            if (!GameTicker.IsGameRuleActive(uid, gameRule))
                continue;

            if (isSolfed)
            {
                rule.SolfedTickets = Math.Max(0, rule.SolfedTickets - cost);
                Log.Info($"[TicketBattle] Solfed player KIA → -{cost} ticket(s) → {rule.SolfedTickets} remaining");
            }
            else
            {
                rule.SyndyTickets = Math.Max(0, rule.SyndyTickets - cost);
                Log.Info($"[TicketBattle] Syndy player KIA → -{cost} ticket(s) → {rule.SyndyTickets} remaining");
            }

            SyncTicketsComponent(uid, rule);
            CheckVictory(uid, rule);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the ticket cost for <paramref name="mob"/>, defaulting to 1.
    /// </summary>
    private int GetTicketCost(EntityUid mob)
    {
        return TryComp<RoleTicketCostComponent>(mob, out var cost) ? cost.TicketCost : 1;
    }

    /// <summary>
    /// Resolves a team name ("Solfed" / "Syndy") from a job ID by prefix.
    /// </summary>
    private static string? ResolveTeam(string jobId)
    {
        if (jobId.StartsWith("Solfed", StringComparison.OrdinalIgnoreCase))
            return "Solfed";
        if (jobId.StartsWith("Syndy", StringComparison.OrdinalIgnoreCase))
            return "Syndy";
        return null;
    }

    /// <summary>
    /// Finds the coordinates of a spawn point entity named "SpawnPoint{team}".
    /// Returns <see cref="EntityCoordinates.Invalid"/> if none found.
    /// </summary>
    private EntityCoordinates FindSpawnPoint(string team)
    {
        var spawnName = $"SpawnPoint{team}";
        foreach (var xform in EntityQuery<TransformComponent>())
        {
            var meta = EntityManager.GetComponentOrNull<MetaDataComponent>(xform.Owner);
            if (meta?.EntityPrototype?.ID == spawnName)
                return xform.Coordinates;
        }
        Log.Warning($"[TicketBattle] Spawn point '{spawnName}' not found.");
        return EntityCoordinates.Invalid;
    }

    /// <summary>
    /// Propagates current ticket counts to <see cref="CaptureTicketsComponent"/> for the client UI.
    /// </summary>
    private void SyncTicketsComponent(EntityUid ruleUid, TicketBattleGameRuleComponent rule)
    {
        if (!TryComp<CaptureTicketsComponent>(ruleUid, out var tickets))
            return;

        tickets.SyndyTickets = rule.SyndyTickets;
        tickets.SolfedTickets = rule.SolfedTickets;
        Dirty(ruleUid, tickets);
    }

    private void CheckVictory(EntityUid ruleUid, TicketBattleGameRuleComponent rule)
    {
        if (rule.Victor != null)
            return;

        if (rule.SolfedTickets <= 0)
        {
            rule.Victor = "Syndy";
            _roundEnd.EndRound(TimeSpan.FromSeconds(10f));
        }
        else if (rule.SyndyTickets <= 0)
        {
            rule.Victor = "Solfed";
            _roundEnd.EndRound(TimeSpan.FromSeconds(10f));
        }
    }

    protected override void AppendRoundEndText(
        EntityUid uid,
        TicketBattleGameRuleComponent component,
        GameRuleComponent gameRule,
        ref RoundEndTextAppendEvent args)
    {
        if (component.Victor == null)
            return;

        var victoryMsg = Loc.GetString($"ticket-battle-victory-{component.Victor.ToLower()}");
        args.AddLine(victoryMsg);
        args.AddLine(Loc.GetString("ticket-battle-score",
            ("solfed", component.SolfedTickets),
            ("syndy", component.SyndyTickets)));
    }
}
