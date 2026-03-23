using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._VXS14.WeaponMonitoring.Components;

[RegisterComponent]
public sealed partial class WeaponMonitoringProfileComponent : Component
{
    [DataField("category", required: true)]
    public WeaponMonitoringCategory Category;

    [DataField("fov")]
    public float? Fov;

    [DataField("seekerType")]
    public string SeekerType = string.Empty;

    [DataField("warheadType")]
    public string WarheadType = string.Empty;

    [DataField("flightTime")]
    public float? FlightTime;

    [DataField("deviation")]
    public float? Deviation;

    [DataField("projectileSpeed")]
    public float? ProjectileSpeed;

    [DataField("notes")]
    public string Notes = string.Empty;
}

[Serializable, NetSerializable]
public enum WeaponMonitoringCategory : byte
{
    AntiShipMissile,
    AerialBomb,
    NavalGun,
    AntiMissile,
}
