namespace Content.Server._VXS.RadarGuidance.Components;

/// <summary>
/// A radar guidance station (РЛС наведения) that locks onto targets and illuminates them
/// for semi-active radar homing (ПАРЛ ГСН) missiles.
/// When a target is locked, a strip-shaped radar marker is added to the target so it is
/// visible on radar displays.
/// </summary>
[RegisterComponent]
public sealed partial class VXSRadarGuidanceStationComponent : Component
{
    /// <summary>
    /// Maximum range (in tiles/units) at which the station can lock onto and illuminate a target.
    /// </summary>
    [DataField]
    public float Range = 800f;

    /// <summary>
    /// The entity currently locked by this guidance station.
    /// </summary>
    [DataField]
    public EntityUid? LockedTarget;

    /// <summary>
    /// Whether this station is currently active (powered / enabled).
    /// </summary>
    [DataField]
    public bool Enabled = true;
}
