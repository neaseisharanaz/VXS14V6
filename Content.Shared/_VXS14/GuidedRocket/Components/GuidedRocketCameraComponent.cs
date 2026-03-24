using Content.Shared.Actions;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Shared._VXS14.GuidedRocket;

[RegisterComponent]
public sealed partial class GuidedRocketCameraComponent : Component
{
    [DataField("returnAction")]
    public EntProtoId ReturnAction = "ActionGuidedRocketReturn";

    [DataField("returnActionEntity")]
    public EntityUid? ReturnActionEntity;

    [ViewVariables]
    public EntityUid? MindId;

    [ViewVariables]
    public EntityUid? ReturnToEntity;

    [ViewVariables]
    public TimeSpan DetonateAt;

    [ViewVariables]
    public string? ImpactEntity;

    [ViewVariables]
    public bool Ending;
}
