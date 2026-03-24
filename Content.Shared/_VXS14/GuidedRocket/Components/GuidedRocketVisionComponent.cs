using Robust.Shared.GameStates;

namespace Content.Shared._VXS14.GuidedRocket;

[RegisterComponent]
[NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GuidedRocketVisionComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite), DataField("enabled"), AutoNetworkedField]
    public bool Enabled { get; set; } = true;
}
