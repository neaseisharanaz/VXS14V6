using System.Numerics;
using Content.Server._VXS.ActiveRadioHeading.Components;
using Content.Server._VXS.RadarGuidance.Components;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._VXS.RadarGuidance.Systems;

/// <summary>
/// Drives <see cref="VXSSemiActiveRadarHomingComponent"/> missiles.
/// Each frame the missile looks for a nearby <see cref="VXSRadarGuidanceStationComponent"/>,
/// inherits its locked target, and homes onto it using the chosen guidance algorithm.
/// When no station lock is available the missile flies inertially (straight ahead).
/// The seeker does NOT respond to countermeasure retarget events, making it fully
/// immune to decoys.
/// </summary>
public sealed class VXSSemiActiveRadarHomingSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly PhysicsSystem _physics = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<VXSSemiActiveRadarHomingComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            // Accelerate to top speed.
            if (comp.Speed < comp.InitialSpeed)
                comp.Speed = comp.InitialSpeed;

            if (comp.Speed < comp.TopSpeed)
                comp.Speed += comp.Acceleration * frameTime;
            else
                comp.Speed = comp.TopSpeed;

            _physics.SetLinearVelocity(uid, _transform.GetWorldRotation(xform).ToWorldVec() * comp.Speed);

            // Try to acquire / maintain station guidance.
            UpdateStationTracking(uid, comp, xform);

            if (comp.TargetEntity.HasValue && !TerminatingOrDeleted(comp.TargetEntity.Value))
            {
                // Active guidance toward station-provided target.
                if ((comp.GuidanceAlgorithm & GuidanceType.PredictiveGuidance) != 0)
                    PredictiveGuidance(uid, comp, xform, frameTime);
                else
                    PurePursuit(uid, comp, xform, frameTime);
            }
            // else: inertial flight — fly straight, no steering
        }
    }

    // -------------------------------------------------------------------------
    // Station tracking
    // -------------------------------------------------------------------------

    private void UpdateStationTracking(
        EntityUid uid,
        VXSSemiActiveRadarHomingComponent comp,
        TransformComponent xform)
    {
        // Revalidate the cached station.
        if (comp.GuidanceStation.HasValue && TerminatingOrDeleted(comp.GuidanceStation.Value))
            comp.GuidanceStation = null;

        // If we don't have a station, search for the closest active one.
        if (!comp.GuidanceStation.HasValue)
            comp.GuidanceStation = FindClosestStation(comp, xform);

        if (!comp.GuidanceStation.HasValue)
        {
            comp.TargetEntity = null;
            return;
        }

        if (!TryComp<VXSRadarGuidanceStationComponent>(comp.GuidanceStation.Value, out var station) ||
            !station.Enabled ||
            !station.LockedTarget.HasValue ||
            TerminatingOrDeleted(station.LockedTarget.Value))
        {
            comp.TargetEntity = null;
            return;
        }

        // Verify target is still within the station's range.
        if (!TryComp<TransformComponent>(station.LockedTarget.Value, out var targetXform) ||
            !TryComp<TransformComponent>(comp.GuidanceStation.Value, out var stationXform))
        {
            comp.TargetEntity = null;
            return;
        }

        var stationPos = _transform.ToMapCoordinates(stationXform.Coordinates).Position;
        var targetPos = _transform.ToMapCoordinates(targetXform.Coordinates).Position;
        var dist = Vector2.Distance(stationPos, targetPos);

        if (dist > station.Range)
        {
            comp.TargetEntity = null;
            return;
        }

        comp.TargetEntity = station.LockedTarget.Value;
    }

    private EntityUid? FindClosestStation(
        VXSSemiActiveRadarHomingComponent comp,
        TransformComponent missileXform)
    {
        var missilePos = _transform.ToMapCoordinates(missileXform.Coordinates).Position;
        var mapId = missileXform.MapID;
        var rangeSq = comp.StationSeekRange * comp.StationSeekRange;

        var closestDist = float.MaxValue;
        EntityUid? best = null;

        var stations = new HashSet<Entity<VXSRadarGuidanceStationComponent>>();
        _lookup.GetEntitiesOnMap(mapId, stations);

        foreach (var station in stations)
        {
            if (!station.Comp.Enabled)
                continue;

            if (!TryComp<TransformComponent>(station.Owner, out var stXform))
                continue;

            var stPos = _transform.ToMapCoordinates(stXform.Coordinates).Position;
            var distSq = Vector2.DistanceSquared(missilePos, stPos);
            if (distSq > rangeSq)
                continue;

            var dist = MathF.Sqrt(distSq);
            if (dist >= closestDist)
                continue;

            closestDist = dist;
            best = station.Owner;
        }

        return best;
    }

    // -------------------------------------------------------------------------
    // Guidance algorithms
    // -------------------------------------------------------------------------

    private void PredictiveGuidance(
        EntityUid uid,
        VXSSemiActiveRadarHomingComponent comp,
        TransformComponent xform,
        float frameTime)
    {
        var entXform = Transform(comp.TargetEntity!.Value);

        var missilePos = _transform.ToMapCoordinates(xform.Coordinates).Position;
        var targetPos = _transform.ToMapCoordinates(entXform.Coordinates).Position;

        var distance = Vector2.Distance(missilePos, targetPos);
        var targetVelocity = targetPos - comp.OldPosition;
        var timeToImpact = distance / Math.Max(comp.OldDistance - distance, 0.01f);
        if (timeToImpact < 0.1f)
            timeToImpact = 0.1f;

        var predictedPosition = targetPos + targetVelocity * timeToImpact;
        var targetAngle = (predictedPosition - missilePos).ToWorldAngle();

        _rotate.TryRotateTo(uid, targetAngle, frameTime, comp.WeaponArc,
            comp.RotationSpeed?.Theta ?? double.MaxValue, xform);

        comp.OldPosition = targetPos;
        comp.OldDistance = distance;
    }

    private void PurePursuit(
        EntityUid uid,
        VXSSemiActiveRadarHomingComponent comp,
        TransformComponent xform,
        float frameTime)
    {
        var entXform = Transform(comp.TargetEntity!.Value);
        var angle = (_transform.ToMapCoordinates(entXform.Coordinates).Position -
                     _transform.ToMapCoordinates(xform.Coordinates).Position).ToWorldAngle();

        _rotate.TryRotateTo(uid, angle, frameTime, comp.WeaponArc,
            comp.RotationSpeed?.Theta ?? double.MaxValue, xform);
    }
}
