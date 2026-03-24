using Content.Server.EUI;
using Content.Shared.Eui;
using Content.Shared._VXS14.AerialBomb;
using Content.Shared._VXS14.GuidedRocket;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.Server._VXS14.GuidedRocket;

[UsedImplicitly]
public sealed class GuidedRocketEui : BaseEui
{
    private readonly EntityUid _rocket;

    public GuidedRocketEui(EntityUid rocket)
    {
        _rocket = rocket;
    }

    public override void Opened()
    {
        base.Opened();
        StateDirty();
    }

    public override EuiStateBase GetNewState()
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var mapSystem = entMan.System<SharedMapSystem>();

        var maps = new List<AerialBombEuiMsg.MapEntry>();

        foreach (var mapId in mapSystem.GetAllMapIds())
        {
            if (!mapSystem.MapExists(mapId))
                continue;

            var mapUid = mapSystem.GetMapOrInvalid(mapId);
            if (!entMan.HasComponent<Content.Shared.Parallax.Biomes.BiomeComponent>(mapUid))
                continue;

            var mapName = entMan.TryGetComponent<MetaDataComponent>(mapUid, out var metadata)
                ? metadata.EntityName
                : $"Map {mapId}";

            maps.Add(new AerialBombEuiMsg.MapEntry(entMan.GetNetEntity(mapUid), mapName));
        }

        maps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return new AerialBombEuiState(maps);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is not AerialBombEuiMsg.DropRequest request)
        {
            Close();
            return;
        }

        var entMan = IoCManager.Resolve<IEntityManager>();
        var rocketSystem = entMan.System<GuidedRocketSystem>();

        if (!entMan.TryGetComponent<SharedGuidedRocketComponent>(_rocket, out var rocketComp) || rocketComp.Launched)
        {
            Close();
            return;
        }

        var targetMapUid = entMan.GetEntity(request.MapEntity);
        if (!entMan.EntityExists(targetMapUid) || !entMan.TryGetComponent<MapComponent>(targetMapUid, out var mapComp))
        {
            Close();
            return;
        }

        rocketSystem.TryLaunch(_rocket, Player.AttachedEntity, mapComp.MapId, rocketComp, Player.UserId);
        Close();
    }
}
