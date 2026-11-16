using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>Actions on one tile of the current map (the agent picks the tile from its map view).</summary>
internal static class TileJobs
{
    private static Farmer Me => Game1.player;

    public static IEnumerator Walk(Job job, Point tile)
    {
        var r = new StepResult();
        if (Steps.IsStandable(Me.currentLocation, tile))
            yield return Steps.WalkTo(tile, r);
        if (!r.Ok)
            yield return Steps.WalkAdjacent(tile, r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? $"can't get to {tile.X},{tile.Y}");
        job.Summary = $"You're at {Me.TilePoint.X},{Me.TilePoint.Y}.";
    }

    /// <summary>"Click" a tile: doors, people, forage, signs, ...</summary>
    public static IEnumerator Use(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var npc = loc.characters.FirstOrDefault(n => n.TilePoint == tile && n.IsVillager);
        if (npc != null)
        {
            yield return MoreJobs.TalkTo(job, npc.Name, null);
            yield break;
        }
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? "can't reach it");
        MoreJobs.EquipTool();
        yield return null;
        var before = Me.currentLocation;
        bool did = loc.checkAction(new xTile.Dimensions.Location(tile.X, tile.Y), Game1.viewport, Me);
        yield return Wait.Seconds(1);
        // Read (and leave for the auto-dismiss) whatever message popped up.
        string? text = (Game1.activeClickableMenu as DialogueBox)?.getCurrentString();
        job.Summary = Me.currentLocation != before
            ? $"You went through to {Observer.LocationName(Me.currentLocation)}."
            : text != null ? $"It says: \"{text.Trim()}\""
            : did ? "Done." : "Nothing happened.";
    }

    /// <summary>Chop / mine / clear whatever is on a tile, with the right tool.</summary>
    public static IEnumerator Hit(Job job, Point tile, string verb)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        string? what = Describe(loc, v);
        if (what == null)
            throw new JobFailed($"there's nothing to {verb} at {tile.X},{tile.Y}");
        if (loc.resourceClumps.Any(c => c.occupiesTile(tile.X, tile.Y)))
            throw new JobFailed($"the {what} at {tile.X},{tile.Y} is too big for your basic tools");

        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        for (int swing = 0; swing < 40; swing++)
        {
            var tool = ToolFor(loc, v);
            if (tool == null)
                break; // gone
            yield return Steps.Swing(tool);
            if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is Tree { falling.Value: true })
                yield return Wait.Seconds(2);
        }
        if (ToolFor(loc, v) != null)
            throw new JobFailed($"the {what} at {tile.X},{tile.Y} won't break");
        yield return Wait.Seconds(0.5);
        // Drops (wood, stone, ...) land around the tile; step onto it to pick them up.
        if (Steps.IsStandable(loc, tile))
            yield return Steps.WalkTo(tile, new StepResult());
        job.Summary = $"Got rid of the {what} at {tile.X},{tile.Y}.";
    }

    private static string? Describe(GameLocation loc, Vector2 v)
    {
        if (loc.resourceClumps.FirstOrDefault(c => c.occupiesTile((int)v.X, (int)v.Y)) is { } clump)
            return clump.parentSheetIndex.Value is 600 or 602 ? "stump" : "boulder";
        if (loc.objects.TryGetValue(v, out var o) && (o.IsWeeds() || o.IsTwig() || o.IsBreakableStone()))
            return o.DisplayName.ToLowerInvariant();
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
            return tf switch { Tree => "tree", Grass => "grass", _ => null };
        return null;
    }

    /// <summary>The tool to use on what's at a tile (null if there's nothing left).</summary>
    private static Tool? ToolFor(GameLocation loc, Vector2 v)
    {
        if (loc.objects.TryGetValue(v, out var o))
        {
            if (o.IsWeeds())
                return Steps.FindScythe() ?? (Tool?)Steps.FindTool<Axe>();
            if (o.IsBreakableStone())
                return Steps.FindTool<Pickaxe>() ?? throw new JobFailed("you don't have a pickaxe");
            if (o.IsTwig())
                return Steps.FindTool<Axe>() ?? throw new JobFailed("you don't have an axe");
        }
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            if (tf is Tree)
                return Steps.FindTool<Axe>() ?? throw new JobFailed("you don't have an axe");
            if (tf is Grass)
                return Steps.FindScythe();
        }
        return null;
    }

    public static IEnumerator Till(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        var hoe = Steps.FindTool<Hoe>() ?? throw new JobFailed("you don't have a hoe");
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt)
            throw new JobFailed($"{tile.X},{tile.Y} is already tilled");
        if (loc.doesTileHaveProperty(tile.X, tile.Y, "Diggable", "Back") == null)
            throw new JobFailed($"{tile.X},{tile.Y} can't be tilled (only soil can)");
        if (Describe(loc, v) != null)
            yield return Hit(job, tile, "clear");
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");
        yield return Steps.Swing(hoe);
        if (!(loc.terrainFeatures.TryGetValue(v, out tf) && tf is HoeDirt))
            throw new JobFailed($"couldn't till {tile.X},{tile.Y}");
        job.Summary = $"Tilled {tile.X},{tile.Y}.";
    }

    public static IEnumerator Water(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var can = Steps.FindTool<WateringCan>() ?? throw new JobFailed("you don't have a watering can");
        if (!loc.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var tf) || tf is not HoeDirt dirt)
            throw new JobFailed($"there's no tilled soil at {tile.X},{tile.Y}");
        if (dirt.isWatered())
        {
            job.Summary = $"{tile.X},{tile.Y} is already watered.";
            yield break;
        }
        if (can.WaterLeft <= 0)
        {
            var refill = new StepResult();
            yield return FarmJobs.Refill(can, refill);
            if (!refill.Ok)
                throw new JobFailed(refill.Error ?? "couldn't refill the watering can");
        }
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");
        yield return Steps.Swing(can);
        job.Summary = dirt.isWatered() ? $"Watered {tile.X},{tile.Y}." : throw new JobFailed($"couldn't water {tile.X},{tile.Y}");
    }

    public static IEnumerator Plant(Job job, Point tile, string? seedName)
    {
        var loc = Me.currentLocation;
        var seeds = FarmJobs.FindSeeds(seedName) ?? throw new JobFailed(seedName == null ? "you have no seeds" : $"you have no '{seedName}'");
        if (!loc.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var tf) || tf is not HoeDirt dirt)
            throw new JobFailed($"{tile.X},{tile.Y} isn't tilled - till it first");
        if (dirt.crop != null)
            throw new JobFailed($"something is already growing at {tile.X},{tile.Y}");
        if (!loc.CanPlantSeedsHere(seeds.ItemId, tile.X, tile.Y, false, out _))
            throw new JobFailed($"{seeds.DisplayName} can't be planted at {tile.X},{tile.Y} (wrong season or place?)");
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");
        Steps.Equip(seeds);
        yield return null;
        if (!dirt.plant(seeds.ItemId, Me, isFertilizer: false))
            throw new JobFailed($"couldn't plant at {tile.X},{tile.Y}");
        Me.Items.ReduceId(seeds.ItemId, 1);
        yield return Wait.Ticks(15);
        job.Summary = $"Planted {seeds.DisplayName} at {tile.X},{tile.Y}.";
    }

    public static IEnumerator Harvest(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        if (!loc.terrainFeatures.TryGetValue(v, out var tf) || tf is not HoeDirt { crop: not null } dirt)
            throw new JobFailed($"there's no crop at {tile.X},{tile.Y}");
        if (!dirt.readyForHarvest())
            throw new JobFailed($"the crop at {tile.X},{tile.Y} isn't ripe yet");
        if (Me.freeSpotsInInventory() == 0)
            throw new JobFailed("your inventory is full");
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");
        string name = Observer.CropName(dirt.crop);
        if (dirt.crop.forageCrop.Value && dirt.crop.whichForageCrop.Value == Crop.forageCrop_gingerID)
            throw new JobFailed($"the {name} at {tile.X},{tile.Y} has to be dug up with a hoe");
        if (dirt.crop.GetHarvestMethod() == StardewValley.GameData.Crops.HarvestMethod.Scythe && Steps.FindScythe() is { } scythe)
            Steps.Equip(scythe);
        dirt.performUseAction(v);
        yield return Wait.Ticks(30);
        if (dirt.crop != null && dirt.readyForHarvest())
            throw new JobFailed($"couldn't harvest the {name} at {tile.X},{tile.Y}");
        job.Summary = $"Harvested the {name} at {tile.X},{tile.Y}.";
    }
}
