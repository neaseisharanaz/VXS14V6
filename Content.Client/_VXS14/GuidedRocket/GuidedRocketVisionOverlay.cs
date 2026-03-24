using Content.Shared._VXS14.GuidedRocket;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._VXS14.GuidedRocket;

public sealed class GuidedRocketVisionOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> ShaderId = "GuidedRocketVisionFullscreen";

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly ILightManager _lightManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;

    public GuidedRocketVisionOverlay()
    {
        IoCManager.InjectDependencies(this);
        _shader = _prototypeManager.Index(ShaderId).InstanceUnique();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_entityManager.TryGetComponent(_playerManager.LocalSession?.AttachedEntity, out EyeComponent? eyeComp))
            return false;

        if (args.Viewport.Eye != eyeComp.Eye)
            return false;

        var playerEntity = _playerManager.LocalSession?.AttachedEntity;
        if (playerEntity == null)
            return false;

        if (!_entityManager.TryGetComponent<GuidedRocketVisionComponent>(playerEntity, out var visionComp) || !visionComp.Enabled)
        {
            _lightManager.DrawHardFov = true;
            _lightManager.DrawLighting = true;
            _lightManager.DrawShadows = true;
            return false;
        }

        // Disable hard FoV so the camera sees the whole rendered area, not just
        // the entity's natural vision radius (dark edges looked like opaque static).
        _lightManager.DrawHardFov = false;
        return true;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        _lightManager.DrawHardFov = false;
        _lightManager.DrawLighting = false;
        _lightManager.DrawShadows = false;

        _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);

        var worldHandle = args.WorldHandle;
        worldHandle.UseShader(_shader);
        worldHandle.DrawRect(args.WorldBounds, Color.White);
        worldHandle.UseShader(null);
    }
}
