using System.Numerics;
using Content.Server._VXS14.AerialBomb;
using Content.Server._VXS.ActiveRadioHeading.Components;
using Content.Server._VXS.ActiveRadioHeading.Systems;
using Content.Server._VXS.RadarGuidance.Components;
using Content.Server._VXS.RadarGuidance.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._ADT.SS40k.Turrets;
using Content.Shared._ADT.SS40k.Turrets.Components;
using Content.Shared._VXS14.AerialBomb;
using Content.Shared._VXS14.WeaponMonitoring;
using Content.Shared._VXS14.WeaponMonitoring.Components;
using Content.Shared._VXS.Manpads.Components;
using Content.Shared.Mind;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Projectiles;
using Content.Shared.Trigger.Components.Effects;
using Content.Shared.UserInterface;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._VXS14.WeaponMonitoring;

public sealed class WeaponMonitoringConsoleSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly AerialBombSystem _aerialBombSystem = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedMindSystem _mindSystem = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly VXSActiveRadioHeadingSystem _activeRadioHeading = default!;
    [Dependency] private readonly VXSActiveThrusterRadioHeadingSystem _activeThrusterRadioHeading = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float UpdateInterval = 1.0f;
    private float _updateAccumulator;
    private readonly Dictionary<EntityUid, EntityUid> _pendingRocketTargets = new();

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<WeaponMonitoringConsoleComponent>(WeaponMonitoringConsoleUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnConsoleOpened);
            subs.Event<RequestWeaponMonitoringRefreshMessage>(OnRefreshRequested);
            subs.Event<WeaponMonitoringControlActionMessage>(OnControlAction);
        });

        SubscribeLocalEvent<WeaponMonitoringProfileComponent, AmmoShotEvent>(OnAmmoShot);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _updateAccumulator += frameTime;
        if (_updateAccumulator < UpdateInterval)
            return;

        _updateAccumulator = 0f;

        var query = EntityQueryEnumerator<WeaponMonitoringConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (!_ui.IsUiOpen(uid, WeaponMonitoringConsoleUiKey.Key))
                continue;

            UpdateConsoleUi(uid, xform);
        }
    }

    private void OnConsoleOpened(Entity<WeaponMonitoringConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateConsoleUi(ent.Owner);
    }

    private void OnRefreshRequested(Entity<WeaponMonitoringConsoleComponent> ent, ref RequestWeaponMonitoringRefreshMessage args)
    {
        UpdateConsoleUi(ent.Owner);
    }

    private void OnControlAction(Entity<WeaponMonitoringConsoleComponent> ent, ref WeaponMonitoringControlActionMessage args)
    {
        var actor = args.Actor;
        if (!actor.Valid)
            return;

        if (IsPlanetMap(Transform(ent.Owner).MapID))
            return;

        var target = GetEntity(args.Entity);
        if (!target.Valid || !EntityManager.EntityExists(target))
            return;

        if (!TryComp(ent.Owner, out TransformComponent? consoleXform) ||
            !TryComp(target, out TransformComponent? targetXform) ||
            consoleXform.GridUid != targetXform.GridUid)
        {
            return;
        }

        switch (args.Action)
        {
            case WeaponMonitoringControlAction.SetBombTarget:
                if (!HasComp<SharedAerialBombComponent>(target))
                    return;

                _aerialBombSystem.TryOpenUi(target, actor);
                break;

            case WeaponMonitoringControlAction.ControlGun:
                if (!TryComp<TurretControllableComponent>(target, out var turretComp))
                    return;

                if (turretComp.User is { } && turretComp.User != actor)
                    return;

                RaiseLocalEvent(target, new GettingControlledEvent(actor, ent.Owner));
                _mindSystem.ControlMob(actor, target);
                break;

            case WeaponMonitoringControlAction.LaunchRocket:
                if (!TryComp<GunComponent>(target, out var gun))
                    return;

                if (TryResolveMissileLock(target, out var lockedTarget))
                    _pendingRocketTargets[target] = lockedTarget;

                _gun.AttemptShoot(target, gun);
                if (HasComp<DeleteOnTriggerComponent>(target))
                    QueueDel(target);
                break;
        }
    }

    private void UpdateConsoleUi(EntityUid consoleUid, TransformComponent? consoleXform = null)
    {
        if (!Resolve(consoleUid, ref consoleXform, false))
            return;

        var entries = new List<WeaponMonitoringConsoleEntry>();
        var consoleGrid = consoleXform.GridUid;
        var planetaryMap = IsPlanetMap(consoleXform.MapID);

        if (!planetaryMap && consoleGrid != null)
        {
            var query = EntityQueryEnumerator<WeaponMonitoringProfileComponent, TransformComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out var profile, out var xform, out var meta))
            {
                if (xform.GridUid != consoleGrid)
                    continue;

                entries.Add(new WeaponMonitoringConsoleEntry
                {
                    Entity = GetNetEntity(uid),
                    Coordinates = GetNetCoordinates(xform.Coordinates),
                    Name = meta.EntityName,
                    Category = profile.Category,
                    Fov = profile.Fov,
                    SeekerType = profile.SeekerType,
                    WarheadType = profile.WarheadType,
                    FlightTime = profile.FlightTime,
                    Deviation = profile.Deviation,
                    ProjectileSpeed = profile.ProjectileSpeed,
                    LockedTarget = profile.Category == WeaponMonitoringCategory.AntiShipMissile
                        ? GetLockedTargetDisplayName(uid)
                        : string.Empty,
                    Notes = profile.Notes,
                });
            }
        }

        entries.Sort(static (a, b) =>
        {
            var categoryCompare = a.Category.CompareTo(b.Category);
            if (categoryCompare != 0)
                return categoryCompare;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        _ui.SetUiState(consoleUid, WeaponMonitoringConsoleUiKey.Key, new WeaponMonitoringConsoleState
        {
            Entries = entries,
            ConsoleGrid = consoleGrid == null ? null : GetNetEntity(consoleGrid.Value),
            PlanetaryMap = planetaryMap,
        });
    }

    private bool IsPlanetMap(MapId mapId)
    {
        if (!_map.MapExists(mapId))
            return false;

        var mapUid = _map.GetMapOrInvalid(mapId);
        return HasComp<BiomeComponent>(mapUid);
    }

    private void OnAmmoShot(Entity<WeaponMonitoringProfileComponent> ent, ref AmmoShotEvent args)
    {
        if (ent.Comp.Category != WeaponMonitoringCategory.AntiShipMissile)
            return;

        if (!_pendingRocketTargets.Remove(ent.Owner, out var targetUid))
            return;

        if (TerminatingOrDeleted(targetUid))
            return;

        foreach (var projectile in args.FiredProjectiles)
        {
            if (TryComp<VXSActiveRadioHeadingComponent>(projectile, out var activeHeading))
                _activeRadioHeading.SetNewTarget((projectile, activeHeading), targetUid);

            if (TryComp<VXSActiveThrusterRadioHeadingComponent>(projectile, out var thrusterHeading))
                _activeThrusterRadioHeading.SetNewTarget((projectile, thrusterHeading), targetUid);
        }
    }

    private string GetLockedTargetDisplayName(EntityUid launcher)
    {
        if (!TryResolveMissileLock(launcher, out var target))
            return Loc.GetString("weapon-monitoring-window-value-unknown");

        if (IsCountermeasureEntity(target))
            return "Undefined";

        var grid = ResolveTargetGrid(target);
        if (grid is null)
            return Loc.GetString("weapon-monitoring-window-value-unknown");

        if (EntityManager.TryGetComponent<MetaDataComponent>(grid.Value, out var gridMeta))
            return gridMeta.EntityName;

        return Loc.GetString("weapon-monitoring-window-value-unknown");
    }

    private bool TryResolveMissileLock(EntityUid launcher, out EntityUid target)
    {
        target = default;

        if (!EntityManager.TryGetComponent<TransformComponent>(launcher, out var launcherXform))
            return false;

        var seeker = GetMissileSeekerMode(launcher);
        if (seeker == MissileSeekerMode.None)
            return false;

        switch (seeker)
        {
            case MissileSeekerMode.ActiveRadar:
                return TryResolveActiveRadarTarget(launcher, launcherXform, out target);
            case MissileSeekerMode.ActiveThruster:
                return TryResolveThrusterTarget(launcher, launcherXform, out target);
            case MissileSeekerMode.SemiActiveRadar:
                return TryResolveSemiActiveRadarTarget(launcher, out target);
            default:
                return false;
        }
    }

    private MissileSeekerMode GetMissileSeekerMode(EntityUid launcher)
    {
        if (!TryComp<BallisticAmmoProviderComponent>(launcher, out var ammoProvider))
            return MissileSeekerMode.None;

        EntProtoId? projectileProto = null;

        if (ammoProvider.Entities.Count > 0)
        {
            var cartridge = ammoProvider.Entities[^1];
            if (TryComp<CartridgeAmmoComponent>(cartridge, out var cartAmmo))
                projectileProto = cartAmmo.Prototype;
        }

        if (projectileProto == null && ammoProvider.Proto != null &&
            _prototype.TryIndex<EntityPrototype>(ammoProvider.Proto, out var cartridgeProto) &&
            cartridgeProto.TryGetComponent<CartridgeAmmoComponent>(out var protoAmmo, EntityManager.ComponentFactory))
        {
            projectileProto = protoAmmo.Prototype;
        }

        if (projectileProto == null || !_prototype.TryIndex<EntityPrototype>(projectileProto, out var projectilePrototype))
            return MissileSeekerMode.None;

        if (projectilePrototype.TryGetComponent<VXSActiveRadioHeadingComponent>(out _, EntityManager.ComponentFactory))
            return MissileSeekerMode.ActiveRadar;

        if (projectilePrototype.TryGetComponent<VXSActiveThrusterRadioHeadingComponent>(out _, EntityManager.ComponentFactory))
            return MissileSeekerMode.ActiveThruster;

        if (projectilePrototype.TryGetComponent<VXSSemiActiveRadarHomingComponent>(out _, EntityManager.ComponentFactory))
            return MissileSeekerMode.SemiActiveRadar;

        return MissileSeekerMode.None;
    }

    private bool TryResolveActiveRadarTarget(EntityUid launcher, TransformComponent launcherXform, out EntityUid target)
    {
        target = default;

        var missileComp = new VXSActiveRadioHeadingComponent();
        var shooterGridUid = launcherXform.GridUid;
        var shooterIffType = shooterGridUid.HasValue ? GetGridIffType(shooterGridUid.Value) : null;

        var retargetQuery = EntityQueryEnumerator<VXSRetargetComponent, TransformComponent>();
        if (TryFindClosestTarget(
                missileComp,
                launcherXform,
                retargetQuery,
                shooterGridUid,
                shooterIffType,
                out target))
        {
            return true;
        }

        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        if (!TryFindClosestTarget(
                missileComp,
                launcherXform,
                consoleQuery,
                shooterGridUid,
                shooterIffType,
                out var targetConsole))
        {
            return false;
        }

        if (EntityManager.TryGetComponent<TransformComponent>(targetConsole, out var targetXform) && targetXform.GridUid.HasValue)
        {
            target = targetXform.GridUid.Value;
            return true;
        }

        return false;
    }

    private bool TryResolveThrusterTarget(EntityUid launcher, TransformComponent launcherXform, out EntityUid target)
    {
        target = default;

        var missileComp = new VXSActiveThrusterRadioHeadingComponent();
        var shooterGridUid = launcherXform.GridUid;
        var shooterIffType = shooterGridUid.HasValue ? GetGridIffType(shooterGridUid.Value) : null;

        var retargetQuery = EntityQueryEnumerator<VXSRetargetThrusterComponent, TransformComponent>();
        if (TryFindClosestTarget(
                missileComp,
                launcherXform,
                retargetQuery,
                shooterGridUid,
                shooterIffType,
                out target))
        {
            return true;
        }

        var thrusterQuery = EntityQueryEnumerator<ThrusterComponent, TransformComponent>();
        if (!TryFindClosestThrusterTarget(
                missileComp,
                launcherXform,
                thrusterQuery,
                shooterGridUid,
                shooterIffType,
                out var targetThruster))
        {
            return false;
        }

        if (EntityManager.TryGetComponent<TransformComponent>(targetThruster, out var targetXform) && targetXform.GridUid.HasValue)
        {
            target = targetXform.GridUid.Value;
            return true;
        }

        return false;
    }

    private bool TryFindClosestTarget<T>(
        VXSActiveRadioHeadingComponent missileComp,
        TransformComponent missileXform,
        EntityQueryEnumerator<T, TransformComponent> query,
        EntityUid? shooterGridUid,
        VXSManpadsIffType? shooterIffType,
        out EntityUid target)
        where T : IComponent
    {
        var closestDistance = float.MaxValue;
        EntityUid? closestTargetUid = null;

        var missilePos = _transform.ToMapCoordinates(missileXform.Coordinates).Position;
        var worldRotation = _transform.GetWorldRotation(missileXform);
        var halfFovRad = missileComp.FOV * Math.PI / 180f;
        var seekRangeSq = missileComp.SeekRange * missileComp.SeekRange;

        while (query.MoveNext(out var candidateUid, out _, out var candidateXform))
        {
            var targetPos = _transform.ToMapCoordinates(candidateXform.Coordinates).Position;
            var distanceSq = Vector2.DistanceSquared(missilePos, targetPos);
            if (distanceSq > seekRangeSq)
                continue;

            var angle = (targetPos - missilePos).ToWorldAngle();
            var angleDifference = Angle.ShortestDistance(angle, worldRotation);
            if (Math.Abs(angleDifference) > halfFovRad)
                continue;

            if (shooterGridUid.HasValue && candidateXform.GridUid.HasValue && shooterGridUid.Value == candidateXform.GridUid.Value)
                continue;

            if (shooterIffType.HasValue && candidateXform.GridUid.HasValue &&
                GetGridIffType(candidateXform.GridUid.Value) == shooterIffType.Value)
            {
                continue;
            }

            var distance = MathF.Sqrt(distanceSq);
            if (distance >= closestDistance)
                continue;

            closestDistance = distance;
            closestTargetUid = candidateUid;
        }

        target = closestTargetUid ?? default;
        return closestTargetUid != null;
    }

    private bool TryFindClosestTarget<T>(
        VXSActiveThrusterRadioHeadingComponent missileComp,
        TransformComponent missileXform,
        EntityQueryEnumerator<T, TransformComponent> query,
        EntityUid? shooterGridUid,
        VXSManpadsIffType? shooterIffType,
        out EntityUid target)
        where T : IComponent
    {
        var closestDistance = float.MaxValue;
        EntityUid? closestTargetUid = null;

        var missilePos = _transform.ToMapCoordinates(missileXform.Coordinates).Position;
        var worldRotation = _transform.GetWorldRotation(missileXform);
        var halfFovRad = missileComp.FOV * Math.PI / 180f;
        var seekRangeSq = missileComp.SeekRange * missileComp.SeekRange;

        while (query.MoveNext(out var candidateUid, out _, out var candidateXform))
        {
            var targetPos = _transform.ToMapCoordinates(candidateXform.Coordinates).Position;
            var distanceSq = Vector2.DistanceSquared(missilePos, targetPos);
            if (distanceSq > seekRangeSq)
                continue;

            var angle = (targetPos - missilePos).ToWorldAngle();
            var angleDifference = Angle.ShortestDistance(angle, worldRotation);
            if (Math.Abs(angleDifference) > halfFovRad)
                continue;

            if (shooterGridUid.HasValue && candidateXform.GridUid.HasValue && shooterGridUid.Value == candidateXform.GridUid.Value)
                continue;

            if (shooterIffType.HasValue && candidateXform.GridUid.HasValue &&
                GetGridIffType(candidateXform.GridUid.Value) == shooterIffType.Value)
            {
                continue;
            }

            var distance = MathF.Sqrt(distanceSq);
            if (distance >= closestDistance)
                continue;

            closestDistance = distance;
            closestTargetUid = candidateUid;
        }

        target = closestTargetUid ?? default;
        return closestTargetUid != null;
    }

    private bool TryFindClosestThrusterTarget(
        VXSActiveThrusterRadioHeadingComponent missileComp,
        TransformComponent missileXform,
        EntityQueryEnumerator<ThrusterComponent, TransformComponent> query,
        EntityUid? shooterGridUid,
        VXSManpadsIffType? shooterIffType,
        out EntityUid target)
    {
        var closestDistance = float.MaxValue;
        EntityUid? closestTargetUid = null;
        var curTime = _timing.CurTime;

        var missilePos = _transform.ToMapCoordinates(missileXform.Coordinates).Position;
        var worldRotation = _transform.GetWorldRotation(missileXform);
        var halfFovRad = missileComp.FOV * Math.PI / 180f;
        var seekRangeSq = missileComp.SeekRange * missileComp.SeekRange;

        while (query.MoveNext(out var candidateUid, out var thruster, out var candidateXform))
        {
            if (!thruster.Firing && curTime - thruster.LastFiringTime > missileComp.RetargetWindow)
                continue;

            var targetPos = _transform.ToMapCoordinates(candidateXform.Coordinates).Position;
            var distanceSq = Vector2.DistanceSquared(missilePos, targetPos);
            if (distanceSq > seekRangeSq)
                continue;

            var angle = (targetPos - missilePos).ToWorldAngle();
            var angleDifference = Angle.ShortestDistance(angle, worldRotation);
            if (Math.Abs(angleDifference) > halfFovRad)
                continue;

            if (shooterGridUid.HasValue && candidateXform.GridUid.HasValue && shooterGridUid.Value == candidateXform.GridUid.Value)
                continue;

            if (shooterIffType.HasValue && candidateXform.GridUid.HasValue &&
                GetGridIffType(candidateXform.GridUid.Value) == shooterIffType.Value)
            {
                continue;
            }

            var distance = MathF.Sqrt(distanceSq);
            if (distance >= closestDistance)
                continue;

            closestDistance = distance;
            closestTargetUid = candidateUid;
        }

        target = closestTargetUid ?? default;
        return closestTargetUid != null;
    }

    private bool TryResolveSemiActiveRadarTarget(EntityUid launcher, out EntityUid target)
    {
        target = default;

        if (!EntityManager.TryGetComponent<TransformComponent>(launcher, out var launcherXform))
            return false;

        var launcherPos = _transform.ToMapCoordinates(launcherXform.Coordinates).Position;
        var mapId = launcherXform.MapID;

        // Find the closest active guidance station on the same map.
        var closestDist = float.MaxValue;
        VXSRadarGuidanceStationComponent? bestStation = null;

        var stationQuery = EntityQueryEnumerator<VXSRadarGuidanceStationComponent, TransformComponent>();
        while (stationQuery.MoveNext(out _, out var station, out var stXform))
        {
            if (!station.Enabled || !station.LockedTarget.HasValue)
                continue;

            if (stXform.MapID != mapId)
                continue;

            var stPos = _transform.ToMapCoordinates(stXform.Coordinates).Position;
            var dist = Vector2.Distance(launcherPos, stPos);
            if (dist >= closestDist)
                continue;

            closestDist = dist;
            bestStation = station;
        }

        if (bestStation?.LockedTarget == null)
            return false;

        var lockedTarget = bestStation.LockedTarget.Value;
        if (TerminatingOrDeleted(lockedTarget))
            return false;

        var grid = ResolveTargetGrid(lockedTarget);
        target = grid ?? lockedTarget;
        return true;
    }

    private EntityUid? ResolveTargetGrid(EntityUid target)
    {
        if (TryComp<MapGridComponent>(target, out _))
            return target;

        if (!EntityManager.TryGetComponent<TransformComponent>(target, out var xform))
            return null;

        return xform.GridUid;
    }

    private bool IsCountermeasureEntity(EntityUid target)
    {
        return HasComp<VXSRetargetComponent>(target) || HasComp<VXSRetargetThrusterComponent>(target);
    }

    private VXSManpadsIffType? GetGridIffType(EntityUid gridUid)
    {
        if (TryComp<VXSIffTransponderComponent>(gridUid, out var directTransponder))
            return directTransponder.IffType;

        var children = new HashSet<Entity<VXSIffTransponderComponent>>();
        _lookup.GetChildEntities(gridUid, children);
        foreach (var child in children)
        {
            return child.Comp.IffType;
        }

        return null;
    }

    private enum MissileSeekerMode : byte
    {
        None,
        ActiveRadar,
        ActiveThruster,
        SemiActiveRadar,
    }
}
