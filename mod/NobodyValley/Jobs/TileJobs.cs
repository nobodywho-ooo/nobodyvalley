using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Crops;
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
        if (loc.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var tf) && tf is HoeDirt { crop: not null })
        {
            yield return Harvest(job, tile); // crops and wild spring onions
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

    /// <summary>Chop (swing the axe at) a tile: trees, twigs, stumps - and weeds, which any heavy hitter also cuts.</summary>
    public static IEnumerator Chop(Job job, Point tile)
    {
        var axe = Steps.FindTool<Axe>() ?? throw new JobFailed("you don't have an axe");
        yield return SwingTool(job, tile, axe, "axe", "chopped", AxeTarget);
    }

    /// <summary>Mine (swing the pickaxe at) a tile: stones, ore, boulders - and it flattens empty tilled soil.</summary>
    public static IEnumerator Mine(Job job, Point tile)
    {
        var pick = Steps.FindTool<Pickaxe>() ?? throw new JobFailed("you don't have a pickaxe");
        yield return SwingTool(job, tile, pick, "pickaxe", "broke", PickaxeTarget);
    }

    /// <summary>Till (swing the hoe at) a tile: soil gets tilled (weeds on it get cut too). Grass, stones, twigs or an
    /// already-tilled tile block the hoe - clear those with the right tool first.</summary>
    public static IEnumerator Till(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt)
            throw new JobFailed($"{tile.X},{tile.Y} is already tilled - plant seeds there (plant item x,y)");
        var hoe = Steps.FindTool<Hoe>() ?? throw new JobFailed("you don't have a hoe");
        yield return SwingTool(job, tile, hoe, "hoe", "tilled", HoeTarget);
    }

    /// <summary>Walk to a tile and swing one specific tool at it (never another tool), reporting what happened.
    /// <paramref name="target"/> says what the tool can act on there and how to tell it's done.</summary>
    private static IEnumerator SwingTool(Job job, Point tile, Tool tool, string toolName, string pastVerb,
        Func<GameLocation, Vector2, (string Label, Func<bool> Done)?> target)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        if (target(loc, v) is not { } hit)
        {
            string hint = Describe(loc, v) is { } what
                ? $"you swung the {toolName} at the {what} at {tile.X},{tile.Y}, but it did nothing - use the right tool (axe chops, pickaxe mines, hoe tills, scythe cuts grass/weeds/dead crops)"
                : $"you swung the {toolName} at {tile.X},{tile.Y}, but there's nothing for it there";
            throw new JobFailed(hint);
        }
        // Big stumps/boulders need a specific upgraded tool; refuse instead of missing many swings.
        if (loc.resourceClumps.Any(c => c.occupiesTile((int)v.X, (int)v.Y)) && ClumpTooHard(loc, v, tool) is { } why)
            throw new JobFailed($"the {hit.Label} at {tile.X},{tile.Y} is too tough for your {toolName} - {why}");
        if (PlayerState.OthersClaims().Contains(v))
            throw new JobFailed($"someone else is already working at {tile.X},{tile.Y}");
        PlayerState.SetClaim(v);

        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        for (int swing = 0; swing < 40 && !hit.Done(); swing++)
            yield return Steps.Swing(tool);
        if (!hit.Done())
            throw new JobFailed($"you swung the {toolName} at the {hit.Label} {tile.X},{tile.Y}, but it's still there - it may need an upgraded tool");

        // Drops (wood, stone, ...) land on or next to the tile; step onto it to pick them up.
        yield return Wait.Ticks(15);
        if (Steps.IsStandable(loc, tile))
            yield return Steps.WalkTo(tile, new StepResult());
        job.Summary = $"You {pastVerb} the {hit.Label} at {tile.X},{tile.Y}.";
    }

    /// <summary>What's on a tile, for hints when a tool can't act on it.</summary>
    private static string? Describe(GameLocation loc, Vector2 v)
    {
        if (loc.resourceClumps.FirstOrDefault(c => c.occupiesTile((int)v.X, (int)v.Y)) is { } clump)
            return clump.parentSheetIndex.Value is 600 or 602 ? "stump" : "boulder";
        if (loc.objects.TryGetValue(v, out var o) && (o.IsWeeds() || o.IsTwig() || o.IsBreakableStone()))
            return o.DisplayName.ToLowerInvariant();
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
            return tf switch
            {
                Tree => "tree",
                Grass => "grass",
                HoeDirt { crop: null } => "tilled soil",
                HoeDirt { crop: { } c } when c.dead.Value => "dead crop",
                HoeDirt { crop: { } c } => $"growing {Observer.CropName(c)}",
                _ => null,
            };
        return null;
    }

    /// <summary>What an axe can act on at a tile, and how to tell it's done.</summary>
    private static (string Label, Func<bool> Done)? AxeTarget(GameLocation loc, Vector2 v)
    {
        if (loc.resourceClumps.FirstOrDefault(c => c.occupiesTile((int)v.X, (int)v.Y)) is { } clump && clump.parentSheetIndex.Value is 600 or 602)
            return (clump.parentSheetIndex.Value == 600 ? "stump" : "hollow log",
                () => !loc.resourceClumps.Any(c => c.occupiesTile((int)v.X, (int)v.Y)));
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is Tree)
            return ("tree", () => !loc.terrainFeatures.ContainsKey(v));
        if (loc.objects.TryGetValue(v, out var o))
        {
            if (o.IsTwig()) return ("twig", () => !loc.objects.ContainsKey(v));
            if (o.IsWeeds()) return ("weeds", () => !loc.objects.ContainsKey(v)); // any heavy hitter clears weeds
        }
        return null;
    }

    /// <summary>What a pickaxe can act on at a tile, and how to tell it's done.</summary>
    private static (string Label, Func<bool> Done)? PickaxeTarget(GameLocation loc, Vector2 v)
    {
        if (loc.resourceClumps.Any(c => c.occupiesTile((int)v.X, (int)v.Y)))
            return ("boulder", () => !loc.resourceClumps.Any(c => c.occupiesTile((int)v.X, (int)v.Y)));
        if (loc.objects.TryGetValue(v, out var o))
        {
            if (o.IsBreakableStone()) return ("stone", () => !loc.objects.TryGetValue(v, out var x) || !x.IsBreakableStone());
            if (o.IsWeeds()) return ("weeds", () => !loc.objects.ContainsKey(v));
        }
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt { crop: null })
            return ("tilled soil", () => !loc.terrainFeatures.ContainsKey(v)); // pickaxe un-tills empty soil
        return null;
    }

    /// <summary>What a hoe can act on at a tile, and how to tell it's done.</summary>
    private static (string Label, Func<bool> Done)? HoeTarget(GameLocation loc, Vector2 v)
    {
        if (loc.doesTileHaveProperty((int)v.X, (int)v.Y, "Diggable", "Back") == null)
            return null;                            // not soil at all
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
            return null;                            // grass, tree or already-tilled blocks the hoe
        if (loc.objects.TryGetValue(v, out var o) && !o.IsWeeds())
            return null;                            // a stone/twig blocks the hoe (the hoe cuts weeds itself)
        return ("soil", () => loc.terrainFeatures.TryGetValue(v, out var t) && t is HoeDirt);
    }

    /// <summary>Whether a resource clump needs an upgraded tool for this tool, and what to say if so.</summary>
    private static string? ClumpTooHard(GameLocation loc, Vector2 v, Tool tool)
    {
        var clump = loc.resourceClumps.FirstOrDefault(c => c.occupiesTile((int)v.X, (int)v.Y));
        if (clump == null)
            return null;
        bool tooHard = false;
        if (tool is Axe)
            tooHard = clump.parentSheetIndex.Value switch { 600 => tool.upgradeLevel.Value < 1, 602 => tool.upgradeLevel.Value < 2, _ => false };
        else if (tool is Pickaxe)
            tooHard = clump.parentSheetIndex.Value switch
            {
                672 => tool.upgradeLevel.Value < 2,
                148 or 622 => tool.upgradeLevel.Value < 3,
                752 or 754 or 756 or 758 => tool.upgradeLevel.Value < 4,
                _ => false,
            };
        return tooHard ? "it needs an upgraded tool" : null;
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

    /// <summary>Just swing the scythe at a tile: cuts grass (hay into the silo), clears weeds, removes dead crops,
    /// or harvests scythe crops (wheat, fiber, ...). Unlike <see cref="Hit"/>, it never switches tools - it only
    /// ever uses the scythe, so it works on things the scythe itself can do.</summary>
    public static IEnumerator Scythe(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        var scythe = Steps.FindScythe() ?? throw new JobFailed("you don't have a scythe");

        if (ScytheTarget(loc, v) is not { } target)
        {
            string hint = loc.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt { crop: { } live } && !live.dead.Value
                ? $"the scythe does nothing to the growing {Observer.CropName(live)} at {tile.X},{tile.Y} - wait until it's ripe and harvest it"
                : Describe(loc, v) is { } other
                    ? $"there's a {other} at {tile.X},{tile.Y} - the scythe only cuts grass, weeds, dead crops and scythe crops like wheat; use clear/chop/mine for the {other}"
                    : $"there's nothing for the scythe at {tile.X},{tile.Y}";
            throw new JobFailed(hint);
        }
        if (PlayerState.OthersClaims().Contains(v))
            throw new JobFailed($"someone else is already working on the {target.Name} at {tile.X},{tile.Y}");
        PlayerState.SetClaim(v);

        int hayBefore = Game1.getFarm().piecesOfHay.Value;
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        for (int swing = 0; swing < 8 && !target.Done(); swing++)
            yield return Steps.Swing(scythe);
        if (!target.Done())
            throw new JobFailed($"the scythe didn't finish the {target.Name} at {tile.X},{tile.Y}");

        // Scythed crops (wheat, fiber, ...) drop next to the tile; step onto them to pick them up.
        if (target.Kind == "ripe crop" && Steps.IsStandable(loc, tile))
            yield return Steps.WalkTo(tile, new StepResult());

        int hay = Game1.getFarm().piecesOfHay.Value - hayBefore;
        job.Summary = target.Kind switch
        {
            "grass" => hay > 0
                ? $"Scythed the grass at {tile.X},{tile.Y}; {hay} hay went into the silo."
                : $"Scythed the grass at {tile.X},{tile.Y} (no hay - is the silo full? feed the animals or build another one).",
            "weeds" => $"Cleared the weeds at {tile.X},{tile.Y} with the scythe.",
            "dead crop" => $"Scythed away the {target.Name} at {tile.X},{tile.Y}; the soil is clear for replanting.",
            _ => $"Harvested the {target.Name} at {tile.X},{tile.Y} with the scythe.",
        };
    }

    /// <summary>What a scythe can do at a tile: the kind, a description, and an "is it done" check.</summary>
    private static (string Kind, string Name, Func<bool> Done)? ScytheTarget(GameLocation loc, Vector2 v)
    {
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is Grass)
            return ("grass", "grass", () => !loc.terrainFeatures.ContainsKey(v));
        if (loc.objects.TryGetValue(v, out var obj) && obj.IsWeeds())
            return ("weeds", obj.DisplayName.ToLowerInvariant(), () => !loc.objects.ContainsKey(v));
        if (loc.terrainFeatures.TryGetValue(v, out var cropTf) && cropTf is HoeDirt { crop: { } crop })
        {
            if (crop.dead.Value)
                return ("dead crop", $"dead {Observer.CropName(crop)}", () => !loc.terrainFeatures.TryGetValue(v, out var now) || now is not HoeDirt { crop: not null });
            if (crop.GetHarvestMethod() == HarvestMethod.Scythe)
                return ("ripe crop", Observer.CropName(crop), () => !loc.terrainFeatures.TryGetValue(v, out var now) || now is not HoeDirt { crop: not null });
        }
        return null;
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
