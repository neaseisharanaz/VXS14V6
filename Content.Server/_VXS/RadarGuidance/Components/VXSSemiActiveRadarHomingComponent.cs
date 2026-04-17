using System.Numerics;
using Content.Server._VXS.ActiveRadioHeading.Components;

namespace Content.Server._VXS.RadarGuidance.Components;

/// <summary>
/// Semi-Active Radar Homing (ПАРЛ ГСН) missile component.
/// The missile is guided toward the target locked by the nearest
/// <see cref="VXSRadarGuidanceStationComponent"/> on the same map while the target
/// remains within the station's range.  If the station loses its lock or the target
/// leaves the illuminated zone the missile automatically switches to inertial flight
/// (i.e. continues straight ahead).  The seeker is completely immune to countermeasures.
/// </summary>
[RegisterComponent]
public sealed partial class VXSSemiActiveRadarHomingComponent : Component
{
    /// <summary>How far the missile searches for a guidance station at launch (tiles).</summary>
    [DataField]
    public float StationSeekRange = 2000f;

    /// <summary>Seeker weapon arc (ignored while being guided externally, used for rotation clamping).</summary>
    [DataField]
    public Angle WeaponArc = Angle.FromDegrees(360);

    /// <summary>Maximum rotation speed of the missile in radians/s. Null means unlimited.</summary>
    [DataField]
    public Angle? RotationSpeed = 40f;

    /// <summary>Guidance algorithm (PredictiveGuidance or PurePursuit).</summary>
    [DataField]
    public GuidanceType GuidanceAlgorithm = GuidanceType.PredictiveGuidance;

    /// <summary>Acceleration in m/s².</summary>
    [DataField]
    public float Acceleration = 8f;

    /// <summary>Top speed in m/s.</summary>
    [DataField]
    public float TopSpeed = 80f;

    /// <summary>Speed at launch in m/s.</summary>
    [DataField]
    public float InitialSpeed = 20f;

    /// <summary>Current speed (runtime state).</summary>
    [DataField]
    public float Speed;

    // --- Runtime state (not serialised) ---

    /// <summary>The guidance station currently providing illumination data to this missile.</summary>
    public EntityUid? GuidanceStation;

    /// <summary>
    /// Target entity as reported by the guidance station.
    /// Null when in inertial flight.
    /// </summary>
    public EntityUid? TargetEntity;

    /// <summary>Previous distance to target, used by predictive guidance.</summary>
    public float OldDistance;

    /// <summary>Previous target position, used by predictive guidance.</summary>
    public Vector2 OldPosition;
}
