using System.Numerics;
using Content.Server._VXS.RadarGuidance.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._VXS.Manpads.Components;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;

namespace Content.Server._VXS.RadarGuidance.Systems;

/// <summary>
/// Manages <see cref="VXSRadarGuidanceStationComponent"/> entities.
/// Each tick the station searches for the closest valid grid target within its range and
/// locks onto it.  While a lock is maintained the target entity receives a
/// <see cref="RadarMarkerComponent"/> drawn as a horizontal strip so radar operators can see
/// that the target is being illuminated.
/// </summary>
public sealed class VXSRadarGuidanceStationSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    // Marker colour used for the illumination strip on the locked target.
    private static readonly Color IlluminationColor = Color.Cyan;

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<VXSRadarGuidanceStationComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var station, out var xform))
        {
            if (!station.Enabled)
            {
                ClearLock(station);
                continue;
            }

            var newTarget = FindBestTarget(station, xform);

            if (newTarget == station.LockedTarget)
                continue; // no change

            // Remove illumination marker from the old target.
            ClearLock(station);

            station.LockedTarget = newTarget;

            // Add illumination strip marker to new target.
            if (newTarget.HasValue)
                ApplyIlluminationMarker(newTarget.Value);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private EntityUid? FindBestTarget(VXSRadarGuidanceStationComponent station, TransformComponent stationXform)
    {
        var stationPos = _transform.ToMapCoordinates(stationXform.Coordinates).Position;
        var rangeSq = station.Range * station.Range;
        var shooterGridUid = stationXform.GridUid;
        var shooterIffType = shooterGridUid.HasValue ? GetGridIffType(shooterGridUid.Value) : null;

        var closestDist = float.MaxValue;
        EntityUid? best = null;

        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (consoleQuery.MoveNext(out var targetUid, out _, out var targetXform))
        {
            var targetPos = _transform.ToMapCoordinates(targetXform.Coordinates).Position;
            var distSq = Vector2.DistanceSquared(stationPos, targetPos);
            if (distSq > rangeSq)
                continue;

            var targetGridUid = targetXform.GridUid;
            if (shooterGridUid.HasValue && targetGridUid.HasValue && shooterGridUid.Value == targetGridUid.Value)
                continue; // same grid

            if (shooterIffType.HasValue && targetGridUid.HasValue &&
                GetGridIffType(targetGridUid.Value) == shooterIffType.Value)
                continue; // friendly IFF

            var dist = MathF.Sqrt(distSq);
            if (dist >= closestDist)
                continue;

            closestDist = dist;
            best = targetGridUid ?? targetUid;
        }

        return best;
    }

    private void ClearLock(VXSRadarGuidanceStationComponent station)
    {
        if (!station.LockedTarget.HasValue)
            return;

        var old = station.LockedTarget.Value;
        station.LockedTarget = null;

        if (TerminatingOrDeleted(old))
            return;

        // Remove our illumination marker.
        if (TryComp<RadarMarkerComponent>(old, out var marker) &&
            marker.Color == IlluminationColor &&
            marker.Shape == RadarShape.Line)
        {
            RemComp<RadarMarkerComponent>(old);
        }
    }

    private void ApplyIlluminationMarker(EntityUid target)
    {
        if (TerminatingOrDeleted(target))
            return;

        // If already has a marker with different settings (e.g. from another system), don't overwrite it.
        if (HasComp<RadarMarkerComponent>(target))
            return;

        var marker = AddComp<RadarMarkerComponent>(target);
        marker.Enabled = true;
        marker.Shape = RadarShape.Line;
        marker.Color = IlluminationColor;
        marker.ShowName = false;
        Dirty(target, marker);
    }

    private VXSManpadsIffType? GetGridIffType(EntityUid gridUid)
    {
        var children = new HashSet<Entity<VXSIffTransponderComponent>>();
        _lookup.GetChildEntities(gridUid, children);
        foreach (var child in children)
            return child.Comp.IffType;
        return null;
    }
}
