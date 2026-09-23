using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BridgeFixtures;

internal static class MapFixture
{
    public static async Task Run(NGame game, RunManager manager, RunState run, bool boss)
    {
        ActMap map = run.Map!;
        MapPoint source = boss
            ? map.GetPointsInRow(map.GetRowCount() - 1).First()
            : map.StartingMapPoint;
        MapPoint destination = boss ? map.BossMapPoint : source.Children.First();
        // Establish the coordinate in the disposable run, then complete an
        // actual rest and travel only via ordinary bridge commands. The normal
        // case uses a rest room at the starting coordinate to test grid travel.
        await manager.EnterMapCoordDebug(source.coord, RoomType.RestSite, showTransition: false);
        // Debug entry skips AfterMapLocationChanged, unlike normal travel.
        manager.MapSelectionSynchronizer.OnLocationChanged(run.MapLocation);
        manager.RunLocationTargetedBuffer.OnLocationChanged(run.RunLocation);
        int votes = 0;
        manager.MapSelectionSynchronizer.PlayerVoteChanged += (_, _, vote) =>
        {
            if (vote.HasValue)
            {
                votes++;
                File.WriteAllText("/test/fixture-map-vote.json", JsonSerializer.Serialize(new
                {
                    count = votes, destination = Id(vote.Value.coord),
                }));
            }
        };
        var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint);
        if (map.SecondBossMapPoint is { } secondBoss)
            points = points.Append(secondBoss);
        File.WriteAllText("/test/fixture-ready.json", JsonSerializer.Serialize(new
        {
            source = Id(source.coord), destination = Id(destination.coord),
            start = Id(map.StartingMapPoint.coord), boss = Id(map.BossMapPoint.coord),
            points = points.Select(p => Id(p.coord)).ToArray(),
        }));

        await WaitForFile(game, "/test/fixture-map-view");
        NMapScreen screen = NMapScreen.Instance!;
        screen.Open(isOpenedFromTopBar: true);
        await WaitForFile(game, "/test/fixture-map-close");
        screen.Close(animateOut: false);
        await WaitForFile(game, "/test/fixture-map-check");
        // Compare the export with the native dictionary/buttons, independently
        // of the bridge's scene-tree traversal and coordinate parser.
        var nodes = (Dictionary<MapCoord, NMapPoint>)typeof(NMapScreen)
            .GetField("_mapPointDictionary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!;
        File.WriteAllText("/test/fixture-map-native.json", JsonSerializer.Serialize(new
        {
            points = nodes.Keys.Select(Id).ToArray(),
            travelable = nodes.Where(p => p.Value.IsEnabled).Select(p => Id(p.Key)).ToArray(),
        }));
        await WaitForFile(game, "/test/fixture-stop");
        if (votes != 1 || run.CurrentMapCoord != destination.coord)
            throw new InvalidOperationException("Expected exactly one native vote and entry into the selected room.");
    }

    private static string Id(MapCoord coord) => $"point_{coord.col}_{coord.row}";

    private static async Task WaitForFile(NGame game, string path)
    {
        while (!File.Exists(path))
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
