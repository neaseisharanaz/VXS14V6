using Content.Shared.GameTicking;
using Content.Shared._VXS14.GuidedRocket;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Player;

namespace Content.Client._VXS14.GuidedRocket;

public sealed class GuidedRocketVisionSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IOverlayManager _overlayManager = default!;
    [Dependency] private readonly ILightManager _lightManager = default!;

    private GuidedRocketVisionOverlay _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GuidedRocketVisionComponent, ComponentInit>(OnVisionInit);
        SubscribeLocalEvent<GuidedRocketVisionComponent, ComponentShutdown>(OnVisionShutdown);
        SubscribeLocalEvent<GuidedRocketVisionComponent, LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<GuidedRocketVisionComponent, LocalPlayerDetachedEvent>(OnPlayerDetached);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);

        _overlay = new GuidedRocketVisionOverlay();
    }

    private void OnVisionInit(EntityUid uid, GuidedRocketVisionComponent component, ComponentInit args)
    {
        if (_player.LocalEntity == uid && component.Enabled)
            _overlayManager.AddOverlay(_overlay);
    }

    private void OnVisionShutdown(EntityUid uid, GuidedRocketVisionComponent component, ComponentShutdown args)
    {
        if (_player.LocalEntity != uid)
            return;

        _overlayManager.RemoveOverlay(_overlay);
        ResetLighting();
    }

    private void OnPlayerAttached(EntityUid uid, GuidedRocketVisionComponent component, LocalPlayerAttachedEvent args)
    {
        if (component.Enabled)
            _overlayManager.AddOverlay(_overlay);
    }

    private void OnPlayerDetached(EntityUid uid, GuidedRocketVisionComponent component, LocalPlayerDetachedEvent args)
    {
        _overlayManager.RemoveOverlay(_overlay);
        ResetLighting();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        ResetLighting();
    }

    private void ResetLighting()
    {
        _lightManager.DrawHardFov = true;
        _lightManager.DrawLighting = true;
        _lightManager.DrawShadows = true;
    }
}
