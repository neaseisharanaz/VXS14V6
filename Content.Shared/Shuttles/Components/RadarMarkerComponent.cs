using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.Components;

[Serializable, NetSerializable]
public enum RadarShape
{
    Circle,
    Square,
    Triangle,
    /// <summary>
    /// A horizontal bar/strip, used to show radar illumination locks.
    /// </summary>
    Line,
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RadarMarkerComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    [DataField, AutoNetworkedField]
    public RadarShape Shape = RadarShape.Circle;

    [DataField, AutoNetworkedField]
    public Color Color = Color.Red;

    [DataField, AutoNetworkedField]
    public bool ShowName = false;
}
