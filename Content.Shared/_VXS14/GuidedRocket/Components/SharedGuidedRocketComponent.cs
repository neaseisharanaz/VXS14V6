using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Shared._VXS14.GuidedRocket;

[RegisterComponent, AutoGenerateComponentState]
public sealed partial class SharedGuidedRocketComponent : Component
{
    [DataField("flightTime"), AutoNetworkedField]
    public float FlightTime = 10f;

    [DataField("guidanceTime"), AutoNetworkedField]
    public float GuidanceTime = 6f;

    [DataField("arrivalSound"), AutoNetworkedField]
    public string? ArrivalSound = "/Audio/Weapons/Guns/Artillery/mortarflyby.ogg";

    [DataField("impactEntity"), AutoNetworkedField]
    public string? ImpactEntity;

    [DataField("signalTargetMapName"), AutoNetworkedField]
    public string? SignalTargetMapName;

    [DataField("cameraPrototype"), AutoNetworkedField]
    public EntProtoId CameraPrototype = "GuidedRocketCamera";

    [ViewVariables(VVAccess.ReadWrite)]
    public bool Launched;
}
