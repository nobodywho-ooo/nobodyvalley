using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>Fishing and the mines.</summary>
internal static class AdventureJobs
{
    private static Farmer Me => Game1.player;

    // ================================================================ fishing

    /// <summary>
    /// Fish from the nearest shore. The cast, the wait and the catch are animated, but the catch itself is
    /// simulated (no reeling minigame): the fish comes from the game's own fish tables for this spot,
    /// season, weather and time of day.
    /// </summary>
    public static IEnumerator Fish(Job job, string? where, int count)
    {
        var rod = Steps.FindTool<FishingRod>() ?? throw new JobFailed("you don't have a fishing rod - Willy sells one (Training Rod, 25g) at his fish shop on the beach");
        if (where != null)
        {
            var r = new StepResult();
            yield return Steps.GoToLocation(Places.Find(where)?.Location ?? where, r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? $"couldn't get to {where}");
        }

        var loc = Me.currentLocation;
        if (!loc.canFishHere())
            throw new JobFailed($"you can't fish in {Observer.LocationName(loc)}");
        var spot = FishingSpots(loc).OrderBy(s => Steps.Dist(s.stand, Me.TilePoint)).Take(6).ToList();
        if (spot.Count == 0)
            throw new JobFailed($"no good fishing spot in {Observer.LocationName(loc)} (try the Beach, the Mountain lake or the Forest river)");

        (Point stand, Point water, int facing) chosen = default;
        bool there = false;
        foreach (var s in spot)
        {
            var walk = new StepResult();
            yield return Steps.WalkTo(s.stand, walk, s.facing);
            if (walk.Ok)
            {
                chosen = s;
                there = true;
                break;
            }
        }
        if (!there)
            throw new JobFailed("couldn't reach the water's edge");

        var caught = new Dictionary<string, int>();
        for (int i = 0; i < count; i++)
        {
            if (Me.Stamina < 10)
                throw new JobFailed("too tired to keep fishing");
            if (Me.freeSpotsInInventory() == 0)
                throw new JobFailed("your inventory is full");

            Me.faceDirection(chosen.facing);
            Steps.Equip(rod);
            yield return null;
            // Cast: the rod's wind-up pose, then the cast animation.
            int facingFrame = chosen.facing switch { 0 => 0, 1 => 1, 2 => 2, _ => 3 };
            Me.FarmerSprite.setCurrentFrame(295 + facingFrame);
            yield return Wait.Seconds(0.8);
            Me.FarmerSprite.animateOnce(295 + facingFrame, 100f, 4);
            Game1.playSound("dropItemInWater");
            Me.Stamina -= 8f - Me.FishingLevel * 0.1f;

            // Wait for a bite.
            yield return Wait.Seconds(4 + Game1.random.NextDouble() * 8);
            Game1.playSound("FishHit");
            yield return Wait.Seconds(1.5);

            var fish = loc.getFish(1000f, null, 4, Me, 0.0, new Vector2(chosen.water.X, chosen.water.Y))
                ?? ItemRegistry.Create("(O)168"); // trash
            Me.FarmerSprite.StopAnimation();
            if (!Me.addItemToInventoryBool(fish))
                throw new JobFailed("your inventory is full");
            Me.holdUpItemThenMessage(fish, showMessage: false);
            Me.gainExperience(Farmer.fishingSkill, fish.Category == SObject.FishCategory ? 12 : 3);
            Game1.playSound("coin");
            caught[fish.DisplayName] = caught.GetValueOrDefault(fish.DisplayName) + 1;
            job.Summary = "Caught " + string.Join(", ", caught.Select(c => $"{c.Value} {c.Key}"));
            yield return Wait.Seconds(2);
            for (int t = 0; t < 300 && (!Me.CanMove || Me.freezePause > 0); t++)
                yield return null; // holding the fish up
            Me.CanMove = true;
        }
        job.Summary += $" in {Observer.LocationName(loc)}.";
    }

    /// <summary>Shore tiles next to fairly open water, with the direction to face.</summary>
    private static IEnumerable<(Point stand, Point water, int facing)> FishingSpots(GameLocation loc)
    {
        var dirs = new[] { (new Point(0, -1), 0), (new Point(1, 0), 1), (new Point(0, 1), 2), (new Point(-1, 0), 3) };
        int w = loc.map.Layers[0].LayerWidth, h = loc.map.Layers[0].LayerHeight;
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
        {
            var stand = new Point(x, y);
            if (loc.isWaterTile(x, y))
                continue;
            foreach (var (d, facing) in dirs)
            {
                // Two tiles of water in front, so the bobber lands in water.
                var w1 = new Point(x + d.X, y + d.Y);
                var w2 = new Point(x + 2 * d.X, y + 2 * d.Y);
                if (loc.isOpenWater(w1.X, w1.Y) && loc.isOpenWater(w2.X, w2.Y) && Steps.IsStandable(loc, stand))
                {
                    yield return (stand, w2, facing);
                    break;
                }
            }
        }
    }

    // ================================================================ mines

    /// <summary>Take the ladder down: from the mine entrance into the mines, or to the next level.</summary>
    public static IEnumerator Descend(Job job)
    {
        var loc = Me.currentLocation;
        if (loc is not (MineShaft or Mine))
        {
            var r = new StepResult();
            yield return Steps.GoToLocation("Mine", r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? "couldn't get to the mines");
            loc = Me.currentLocation;
        }

        if (loc is Mine)
        {
            // The entrance ladder (or the elevator, once you've been deeper).
            var ladder = FarmJobs.FindActionTile(loc, "Mine", exact: true);
            if (ladder != null)
                yield return Steps.WalkAdjacent(ladder.Value, new StepResult());
            int level = Me.deepestMineLevel >= 5 ? Math.Min(120, Me.deepestMineLevel / 5 * 5) : 1;
            Game1.enterMine(level);
            yield return Wait.Seconds(1);
            job.Summary = $"You entered the mines, level {level}.";
            yield break;
        }

        var shaft = (MineShaft)loc;
        var ladderTile = LadderDown(shaft) ?? throw new JobFailed("there's no ladder down here yet - break rocks to find one");
        var w2 = new StepResult();
        yield return Steps.WalkAdjacent(ladderTile, w2);
        if (!w2.Ok)
            throw new JobFailed("can't reach the ladder");
        int next = shaft.mineLevel + 1;
        Game1.enterMine(next);
        shaft.playSound("stairsdown");
        yield return Wait.Seconds(1);
        job.Summary = $"You went down to mine level {next}.";
    }

    public static Point? LadderDown(MineShaft shaft)
    {
        var layer = shaft.map.GetLayer("Buildings");
        for (int x = 0; x < layer.LayerWidth; x++)
        for (int y = 0; y < layer.LayerHeight; y++)
            if (shaft.getTileIndexAt(x, y, "Buildings") == 173)
                return new Point(x, y);
        return null;
    }
}