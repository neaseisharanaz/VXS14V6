using System;
using System.Collections.Generic;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Players;
using Content.Shared.GG.CapturePoint;
using Content.Server.GG.CapturePoint;
using Content.Server.GG.GameTicking.Rules.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Roles;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.GameObjects;
using Content.Shared.GameTicking.Components;
using Content.Server.RoundEnd;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Mind;
using Content.Server.Station.Systems;
using Robust.Server.GameObjects;
using Content.Server.GameTicking.Rules.Components;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.Server.GG.GameTicking.Rules
{
    public sealed class CapturePointRuleSystem : GameRuleSystem<CapturePointGameRuleComponent>
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly GGCapturePointSystem _capturePoints = default!;
        [Dependency] private readonly GameTicker _ticker = default!;

        [Dependency] private readonly MindSystem _mind = default!;

        [Dependency] private readonly RespawnRuleSystem _respawn = default!;

        [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
        [Dependency] private readonly TransformSystem _transform = default!;
        [Dependency] private readonly RoundEndSystem _roundEnd = default!;

        private TimeSpan _nextMinuteTick = TimeSpan.Zero;

        /// <summary>
        /// Словарь: Session → RoleName (должность, с которой игрок впервые зашёл).
        /// </summary>
        private readonly Dictionary<ICommonSession, string> _playerFirstJob = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn);
            SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn);
            SubscribeLocalEvent<GGSyndyTeamComponent, MobStateChangedEvent>(OnSyndyDeath);
            SubscribeLocalEvent<GGSolfedTeamComponent, MobStateChangedEvent>(OnSolfedDeath);
        }


        private void OnBeforeSpawn(PlayerBeforeSpawnEvent ev)
        {
            // Если это не наш режим — ничего не делаем.
            var query = EntityQueryEnumerator<CapturePointGameRuleComponent, RespawnTrackerComponent, GameRuleComponent>();
            while (query.MoveNext(out var uid, out var rule, out var tracker, out var gameRule))
            {
                if (!GameTicker.IsGameRuleActive(uid, gameRule))
                    continue;



                // Сохраняем выбранную работу — для дневника
                if (!_playerFirstJob.ContainsKey(ev.Player))
                {
                                        // Если работы нет → пропускаем.
                    if (ev.JobId == null)
                    {
                        Logger.Info($"[CapturePointRule] Игрок {ev.Player.Name} пропускается");
                        continue;
                    }

                    _playerFirstJob[ev.Player] = ev.JobId;

                    Logger.Info($"[CapturePointRule] Игрок {ev.Player.Name} впервые выбрал должность: {ev.JobId}");

                    Logger.Info("[CapturePointRule] Текущее содержание дневника:");
                    foreach (var pair in _playerFirstJob)
                    {
                        Logger.Info($" - {pair.Key.Name}: {pair.Value}");
                    }

                    rule.PlayerFirstJob = _playerFirstJob;
                }

                var job = _playerFirstJob[ev.Player];
                // ===============================================================
                // 1) Определяем команду по JobId
                // ===============================================================
                var team = ResolveTeam(job);

                if (team == null)
                {
                    Logger.Error($"[CapturePointRule] Неизвестная работа: {job}");
                    continue;
                }

                // ===============================================================
                // 2) Находим спавнпойнт команды
                // ===============================================================

                var spawnName = "SpawnPoint" + team;
                EntityCoordinates spawnCoords = EntityCoordinates.Invalid;

                foreach (var xform in EntityQuery<TransformComponent>())
                {
                    var meta = EntityManager.GetComponentOrNull<MetaDataComponent>(xform.Owner);
                    if (meta?.EntityPrototype?.ID == spawnName)
                    {
                        spawnCoords = xform.Coordinates;
                        break;
                    }
                }

                if (!spawnCoords.IsValid(EntityManager))
                {
                    Logger.Error($"[CapturePointRule] SpawnPoint '{spawnName}' НЕ найден!");
                    spawnCoords = Transform(ev.Station).Coordinates;
                }

                // ===============================================================
                // 3) Создаём mind игрока
                // ===============================================================
                var mind = _mind.CreateMind(ev.Player.UserId, ev.Profile.Name);
                _mind.SetUserId(mind, ev.Player.UserId);

                // ===============================================================
                // 4) Спавним Mоб вручную
                // ===============================================================

                var mobMaybe = _stationSpawning.SpawnPlayerMob(
                    spawnCoords,
                    job,
                    ev.Profile,
                    ev.Station);


                DebugTools.AssertNotNull(mobMaybe);
                var mob = mobMaybe;

                // ===============================================================
                // 5) Присоединяем mind
                // ===============================================================
                _mind.TransferTo(mind, mob);

                // ===============================================================
                // 6) Выдаём командный компонент
                // ===============================================================
                switch (team)
                {
                    case "Syndy":
                        EnsureComp<GGSyndyTeamComponent>(mob);
                        break;

                    case "Solfed":
                        EnsureComp<GGSolfedTeamComponent>(mob);
                        break;
                }

                // ===============================================================
                // 7) Регистрируем в respawn-трекере
                // ===============================================================
                _respawn.AddToTracker(ev.Player.UserId, (uid, tracker));

                // ===============================================================
                // 8) Завершаем стандартный процесс спавна
                // ===============================================================
                ev.Handled = true;

                Logger.Info($"[CapturePointRule] Спавн игрока {ev.Player.Name} на '{spawnName}' как {team}");
                return;
            }
        }

        private static string? ResolveTeam(string jobId)
        {
            if (jobId.StartsWith("Solfed", StringComparison.OrdinalIgnoreCase))
                return "Solfed";

            if (jobId.StartsWith("Syndy", StringComparison.OrdinalIgnoreCase))
                return "Syndy";

            return null;
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            var now = _timing.CurTime;

            if (now < _nextMinuteTick)
                return;

            _nextMinuteTick = now + TimeSpan.FromMinutes(1);

            foreach (var (rule, _) in EntityQuery<CapturePointGameRuleComponent, GameRuleComponent>())
            {
                HandleMinuteTick(rule);
            }
        }

        // ==========================================================
        // 1. ОБРАБОТКА СМЕРТИ
        // ==========================================================

        private void OnSolfedDeath(Entity<GGSolfedTeamComponent> ent, ref MobStateChangedEvent args)
        {
            if (args.NewMobState != MobState.Dead)
                return;

            if (!args.Target.Valid)
                return;

            foreach (var (rule, _) in EntityQuery<CapturePointGameRuleComponent, GameRuleComponent>())
            {
                if (HasComp<GGSolfedTeamComponent>(args.Target))
                {
                    rule.SoledTeamPoints = Math.Max(0, rule.SoledTeamPoints - 1);
                    Logger.Info($"[CapturePointRule] Солфед погиб → -1 очко → {rule.SoledTeamPoints}");
                    if (TryComp<CaptureTicketsComponent>(rule.Owner, out var tickets))
                    {
                        tickets.SyndyTickets = rule.SyndyTeamPoints;
                        tickets.SolfedTickets = rule.SoledTeamPoints;
                        Dirty(rule.Owner, tickets);
                    }
                }

                CheckForVictory(rule);
            }
        }

        private void OnSyndyDeath(Entity<GGSyndyTeamComponent> ent, ref MobStateChangedEvent args)
        {
            if (args.NewMobState != MobState.Dead)
                return;

            if (!args.Target.Valid)
                return;

            foreach (var (rule, _) in EntityQuery<CapturePointGameRuleComponent, GameRuleComponent>())
            {
                if (HasComp<GGSyndyTeamComponent>(args.Target))
                {
                    rule.SyndyTeamPoints = Math.Max(0, rule.SyndyTeamPoints - 1);
                    Logger.Info($"[CapturePointRule] Синдикат погиб → -1 очко → {rule.SyndyTeamPoints}");
                    if (TryComp<CaptureTicketsComponent>(rule.Owner, out var tickets))
                    {
                        tickets.SyndyTickets = rule.SyndyTeamPoints;
                        tickets.SolfedTickets = rule.SoledTeamPoints;
                        Dirty(rule.Owner, tickets);
                    }
                }

                CheckForVictory(rule);
            }
        }

        // ==========================================================
        // 2. РЕГИСТРАЦИЯ ПЕРВОГО КЛАССА ИГРОКА
        // ==========================================================

        private void OnPlayerSpawn(PlayerSpawnCompleteEvent ev)
        {
            var query = EntityQueryEnumerator<CapturePointGameRuleComponent, RespawnTrackerComponent, GameRuleComponent>();
            while (query.MoveNext(out var uid, out _, out var tracker, out var rule))
            {
                if (!GameTicker.IsGameRuleActive(uid, rule))
                    continue;
                _respawn.AddToTracker((ev.Mob, null), (uid, tracker));
        }
        }

        // ==========================================================
        // 3. ОБРАБОТКА МИНУТНОГО ТИКА
        // ==========================================================

        private void HandleMinuteTick(CapturePointGameRuleComponent comp)
        {
            var (syndyCap, solfedCap) = ReadCapturedPoints();

            int syndyDelta = -1;
            int solfedDelta = -1;

            if (syndyCap >= 2) syndyDelta = 0;
            if (solfedCap >= 2) solfedDelta = 0;

            if (syndyCap >= 3) syndyDelta = +1;
            if (solfedCap >= 3) solfedDelta = +1;

            comp.SyndyTeamPoints = Math.Max(0, comp.SyndyTeamPoints + syndyDelta);
            comp.SoledTeamPoints = Math.Max(0, comp.SoledTeamPoints + solfedDelta);

            Logger.Info($"[CapturePointRule] Минутный тик: Syndy={comp.SyndyTeamPoints} ({syndyDelta:+#;-#;0}), " +
                        $"Solfed={comp.SoledTeamPoints} ({solfedDelta:+#;-#;0})");

        if (TryComp<CaptureTicketsComponent>(comp.Owner, out var tickets))
        {
            tickets.SyndyTickets = comp.SyndyTeamPoints;
            tickets.SolfedTickets = comp.SoledTeamPoints;
            Dirty(comp.Owner, tickets);
        }
            CheckForVictory(comp);
        }

        private (int syndy, int solfed) ReadCapturedPoints()
        {
            var pts = _capturePoints.GetTeamPoints();
            return ((int)pts["Syndy"], (int)pts["Solfed"]);
        }

        // ==========================================================
        // 4. ПРОВЕРКА ПОБЕДЫ
        // ==========================================================

        private void CheckForVictory(CapturePointGameRuleComponent component)
        {
            if (component.Victor != null)
                return;

            if (component.SyndyTeamPoints <= 0)
            {
                component.Victor = "Solfed";
                _roundEnd.EndRound(TimeSpan.FromSeconds(10f));
            }
            else if (component.SoledTeamPoints <= 0)
            {
                component.Victor = "Syndy";
                _roundEnd.EndRound(TimeSpan.FromSeconds(10f));
            }
        }

        protected override void AppendRoundEndText(EntityUid uid, CapturePointGameRuleComponent component, GameRuleComponent gameRule, ref RoundEndTextAppendEvent args)
        {
            if (component.Victor != null)
            {
                var victoryId = $"gg-capturepoint-victory-{component.Victor.ToLower()}";
                var victoryMessage = Loc.GetString(victoryId);

                args.AddLine(victoryMessage);
                args.AddLine($"Syndy: {component.SyndyTeamPoints}");
                args.AddLine($"Solfed: {component.SoledTeamPoints}");
            }
        }

    }
}
