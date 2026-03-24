using Content.Server.EUI;
using Content.Server._VXS14.AerialBomb;
using Content.Shared.Actions;
using Content.Shared.Mind;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Trigger;
using Content.Shared.Verbs;
using Content.Shared._VXS14.GuidedRocket;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._VXS14.GuidedRocket;

public sealed class GuidedRocketSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly AerialBombSystem _aerialBomb = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SharedGuidedRocketComponent, GetVerbsEvent<ExamineVerb>>(OnGetVerb);
        SubscribeLocalEvent<SharedGuidedRocketComponent, TriggerEvent>(OnTriggered);

        SubscribeLocalEvent<GuidedRocketCameraComponent, MapInitEvent>(OnCameraMapInit);
        SubscribeLocalEvent<GuidedRocketCameraComponent, ComponentShutdown>(OnCameraShutdown);
        SubscribeLocalEvent<GuidedRocketCameraComponent, GuidedRocketReturnActionEvent>(OnReturnAction);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<GuidedRocketCameraComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Ending || _timing.CurTime < comp.DetonateAt)
                continue;

            EndGuidance(uid, comp, true);
        }
    }

    private void OnGetVerb(EntityUid uid, SharedGuidedRocketComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        var verb = new ExamineVerb
        {
            Text = Loc.GetString("aerial-bomb-verb-open"),
            Act = () => TryOpenUi(uid, args.User)
        };

        args.Verbs.Add(verb);
    }

    private void OnTriggered(EntityUid uid, SharedGuidedRocketComponent component, ref TriggerEvent args)
    {
        if (TryLaunch(uid, null, null, component))
            args.Handled = true;
    }

    private void OnCameraMapInit(Entity<GuidedRocketCameraComponent> ent, ref MapInitEvent args)
    {
        _actions.AddAction(ent.Owner, ref ent.Comp.ReturnActionEntity, ent.Comp.ReturnAction);
    }

    private void OnCameraShutdown(Entity<GuidedRocketCameraComponent> ent, ref ComponentShutdown args)
    {
        if (!ent.Comp.Ending)
            ReturnOperator(ent.Comp);

        if (ent.Comp.ReturnActionEntity is { } action)
            _actions.RemoveAction(action);
    }

    private void OnReturnAction(Entity<GuidedRocketCameraComponent> ent, ref GuidedRocketReturnActionEvent args)
    {
        EndGuidance(ent.Owner, ent.Comp, false);
    }

    public bool TryOpenUi(EntityUid rocket, EntityUid user)
    {
        if (!TryComp<ActorComponent>(user, out var actor))
            return false;

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new GuidedRocketEui(rocket), actor.PlayerSession);
        return true;
    }

    public bool TryLaunch(
        EntityUid rocket,
        EntityUid? actor,
        MapId? selectedMapId,
        SharedGuidedRocketComponent? comp = null,
        NetUserId? actorUserId = null)
    {
        if (!Resolve(rocket, ref comp, false))
            return false;

        if (comp.Launched)
            return false;

        if (!TryComp<TransformComponent>(rocket, out var xform) || xform.GridUid is null)
            return false;

        if (string.IsNullOrWhiteSpace(comp.ImpactEntity))
            return false;

        var sourcePosition = _transform.GetMapCoordinates(rocket);
        var sourceMapUid = _mapSystem.GetMapOrInvalid(sourcePosition.MapId);

        if (HasComp<BiomeComponent>(sourceMapUid))
            return false;

        if (!_aerialBomb.TryResolveTargetMap(selectedMapId, comp.SignalTargetMapName, out var targetMapId))
            return false;

        var targetPosition = new MapCoordinates(sourcePosition.Position, targetMapId);
        var targetMapUid = _mapSystem.GetMapOrInvalid(targetMapId);
        var targetCoordinates = _transform.ToCoordinates(targetMapUid, targetPosition);

        var flightTime = Math.Max(0.1f, comp.FlightTime);
        var guidanceTime = Math.Max(0.1f, comp.GuidanceTime);
        var impactEntity = comp.ImpactEntity;
        var arrivalSound = comp.ArrivalSound;
        var cameraPrototype = comp.CameraPrototype;

        comp.Launched = true;
        Del(rocket);

        Timer.Spawn(TimeSpan.FromSeconds(flightTime), () =>
        {
            if (!_mapSystem.MapExists(targetMapId))
                return;

            if (!string.IsNullOrWhiteSpace(arrivalSound))
                _audio.PlayPvs(new SoundPathSpecifier(arrivalSound), targetCoordinates);

            var camera = Spawn(cameraPrototype, targetPosition);
            if (!TryComp<GuidedRocketCameraComponent>(camera, out var cameraComp))
            {
                QueueDel(camera);
                return;
            }

            cameraComp.DetonateAt = _timing.CurTime + TimeSpan.FromSeconds(guidanceTime);
            cameraComp.ImpactEntity = impactEntity;

            if (actor is { Valid: true } actorUid)
            {
                AttachOperator(camera, cameraComp, actorUid);
                return;
            }

            if (actorUserId != null)
                AttachOperatorByUserId(camera, cameraComp, actorUserId.Value);
        });

        return true;
    }

    private void AttachOperator(EntityUid camera, GuidedRocketCameraComponent cameraComp, EntityUid actor)
    {
        if (!_mind.TryGetMind(actor, out var mindId, out var mind))
            return;

        if (mind.VisitingEntity != null)
            _mind.UnVisit(mindId, mind);

        var returnToEntity = mind.OwnedEntity;
        _mind.TransferTo(mindId, camera, ghostCheckOverride: true, createGhost: false, mind: mind);
        cameraComp.MindId = mindId;
        cameraComp.ReturnToEntity = returnToEntity;
    }

    private void AttachOperatorByUserId(EntityUid camera, GuidedRocketCameraComponent cameraComp, NetUserId userId)
    {
        if (!_mind.TryGetMind(userId, out var mindId, out var mind))
            return;

        if (mind.VisitingEntity != null)
            _mind.UnVisit(mindId.Value, mind);

        var returnToEntity = mind.OwnedEntity;
        _mind.TransferTo(mindId.Value, camera, ghostCheckOverride: true, createGhost: false, mind: mind);
        cameraComp.MindId = mindId;
        cameraComp.ReturnToEntity = returnToEntity;
    }

    private void EndGuidance(EntityUid uid, GuidedRocketCameraComponent component, bool detonate)
    {
        if (component.Ending)
            return;

        component.Ending = true;

        if (detonate && !string.IsNullOrWhiteSpace(component.ImpactEntity))
            Spawn(component.ImpactEntity, _transform.GetMapCoordinates(uid));

        ReturnOperator(component);
        QueueDel(uid);
    }

    private void ReturnOperator(GuidedRocketCameraComponent component)
    {
        if (component.MindId is not { } mindId || !TryComp<MindComponent>(mindId, out var mind))
            return;

        if (mind.VisitingEntity != null)
            _mind.UnVisit(mindId, mind);

        if (component.ReturnToEntity is { Valid: true } returnTo && EntityManager.EntityExists(returnTo))
            _mind.TransferTo(mindId, returnTo, ghostCheckOverride: true, createGhost: false, mind: mind);

        component.MindId = null;
        component.ReturnToEntity = null;
    }
}
