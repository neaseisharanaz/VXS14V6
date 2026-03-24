using Content.Server.EUI;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Trigger;
using Content.Shared._VXS14.AerialBomb;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._VXS14.AerialBomb;

public sealed class AerialBombSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SharedAerialBombComponent, GetVerbsEvent<ExamineVerb>>(OnGetVerb);
        SubscribeLocalEvent<SharedAerialBombComponent, TriggerEvent>(OnTriggered);
    }

    private void OnGetVerb(EntityUid uid, SharedAerialBombComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        var verb = new ExamineVerb
        {
            Text = Loc.GetString("aerial-bomb-verb-open"),
            Act = () => TryOpenUi(uid, args.User)
        };

        args.Verbs.Add(verb);
    }

    private void OnTriggered(EntityUid uid, SharedAerialBombComponent component, ref TriggerEvent args)
    {
        if (TryDropBomb(uid, null, component))
            args.Handled = true;
    }

    public bool TryOpenUi(EntityUid bomb, EntityUid user)
    {
        if (!_player.TryGetSessionByEntity(user, out var session))
            return false;

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new AerialBombEui(bomb), session);
        return true;
    }

    public bool TryDropBomb(EntityUid bomb, MapId? selectedMapId, SharedAerialBombComponent? comp = null)
    {
        if (!Resolve(bomb, ref comp, false))
            return false;

        if (comp.Dropped)
            return false;

        if (!TryComp<TransformComponent>(bomb, out var xform) || xform.GridUid is null)
            return false;

        if (string.IsNullOrWhiteSpace(comp.ImpactEntity))
            return false;

        var sourcePosition = _transform.GetMapCoordinates(bomb);
        var sourceMapUid = _mapSystem.GetMapOrInvalid(sourcePosition.MapId);

        if (HasComp<BiomeComponent>(sourceMapUid))
            return false;

        if (!TryResolveTargetMap(selectedMapId, comp, out var targetMapId))
            return false;

        var impactOffset = _random.NextVector2(comp.DispersionRadius);
        var impactPosition = new MapCoordinates(sourcePosition.Position + impactOffset, targetMapId);
        var mapUid = _mapSystem.GetMapOrInvalid(targetMapId);
        var impactCoordinates = _transform.ToCoordinates(mapUid, impactPosition);

        var flightTime = Math.Max(0.1f, comp.FlightTime);
        var preImpactDelay = Math.Max(0f, comp.PreImpactDelay);
        var impactEntity = comp.ImpactEntity;
        var arrivalSound = comp.ArrivalSound;

        comp.Dropped = true;
        Del(bomb);

        Timer.Spawn(TimeSpan.FromSeconds(flightTime), () =>
        {
            if (!_mapSystem.MapExists(targetMapId))
                return;

            if (!string.IsNullOrWhiteSpace(arrivalSound))
                _audio.PlayPvs(new SoundPathSpecifier(arrivalSound), impactCoordinates);

            Timer.Spawn(TimeSpan.FromSeconds(preImpactDelay), () =>
            {
                if (!_mapSystem.MapExists(targetMapId))
                    return;

                Spawn(impactEntity, impactPosition);
            });
        });

        return true;
    }

    public bool TryResolveTargetMap(MapId? selectedMapId, string? preferredName, out MapId target)
    {
        if (selectedMapId is { } selected && IsPlanetMap(selected))
        {
            target = selected;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            foreach (var mapId in _mapSystem.GetAllMapIds())
            {
                if (!IsPlanetMap(mapId))
                    continue;

                var mapUid = _mapSystem.GetMapOrInvalid(mapId);
                if (!TryComp<MetaDataComponent>(mapUid, out var meta))
                    continue;

                if (string.Equals(meta.EntityName, preferredName, StringComparison.OrdinalIgnoreCase))
                {
                    target = mapId;
                    return true;
                }
            }
        }

        foreach (var mapId in _mapSystem.GetAllMapIds())
        {
            if (IsPlanetMap(mapId))
            {
                target = mapId;
                return true;
            }
        }

        target = default;
        return false;
    }

    public bool TryResolveTargetMap(MapId? selectedMapId, SharedAerialBombComponent comp, out MapId target)
    {
        return TryResolveTargetMap(selectedMapId, comp.SignalTargetMapName, out target);
    }

    private bool IsPlanetMap(MapId mapId)
    {
        if (!_mapSystem.MapExists(mapId))
            return false;

        var mapUid = _mapSystem.GetMapOrInvalid(mapId);
        return HasComp<BiomeComponent>(mapUid);
    }
}
