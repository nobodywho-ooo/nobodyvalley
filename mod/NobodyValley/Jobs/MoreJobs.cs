using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>Foraging, wood, crafting and socialising.</summary>
internal static class MoreJobs
{
    private static Farmer Me => Game1.player;

    // ================================================================ forage

    /// <summary>Pick up wild forage (leeks, daffodils, ...) in a location (default: where you are).</summary>
    public static IEnumerator Forage(Job job, string? where, int count)
    {
        if (where != null)
        {
            var r = new StepResult();
            var target = Places.Find(where)?.Location ?? where;
            yield return Steps.GoToLocation(target, r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? $"couldn't get to {where}");
        }

        var loc = Me.currentLocation;
        var skipped = new HashSet<Vector2>();
        var found = new Dictionary<string, int>();
        int picked = 0;
        while (picked < count)
        {
            if (Me.freeSpotsInInventory() == 0)
                throw new JobFailed("your inventory is full");
            var here = Me.TilePoint;
            Vector2? next = loc.objects.Pairs
                .Where(p => p.Value.IsSpawnedObject && p.Value.isForage() && !skipped.Contains(p.Key))
                .Select(p => p.Key)
                .OrderBy(t => Steps.Dist(Steps.ToPoint(t), here))
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;

            var tile = next.Value;
            string name = loc.objects[tile].DisplayName;
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(Steps.ToPoint(tile), walk);
            if (!walk.Ok)
            {
                skipped.Add(tile);
                continue;
            }
            EquipTool(); // don't accidentally "use" a held item on it
            loc.checkAction(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport, Me);
            yield return Wait.Ticks(20);
            if (loc.objects.ContainsKey(tile))
            {
                skipped.Add(tile);
                continue;
            }
            picked++;
            found[name] = found.GetValueOrDefault(name) + 1;
            job.Summary = "Foraged " + string.Join(", ", found.Select(f => $"{f.Value} {f.Key}"));
        }
        job.Summary = picked == 0
            ? $"Found nothing to forage in {loc.DisplayName} (try the Forest, Mountain or Beach)."
            : job.Summary + $" in {loc.DisplayName}.";
    }

    public static int ForageCount(GameLocation? loc) =>
        loc?.objects.Values.Count(o => o.IsSpawnedObject && o.isForage()) ?? 0;

    // ================================================================ chop trees

    public static IEnumerator ChopTrees(Job job, int count)
    {
        var axe = Steps.FindTool<Axe>() ?? throw new JobFailed("you don't have an axe");
        if (Me.currentLocation is not Farm)
        {
            var r = new StepResult();
            yield return Steps.GoToLocation("Farm", r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? "couldn't get to the farm");
        }
        var farm = Game1.getFarm();
        var centre = FarmJobs.FieldCentre();
        var skipped = new HashSet<Vector2>();
        int chopped = 0;
        int wood = Me.Items.CountId("(O)388");

        while (chopped < count)
        {
            var claims = PlayerState.OthersClaims();
            // Full-grown trees first (saplings give no wood), nearest to the field.
            Vector2? next = farm.terrainFeatures.Pairs
                .Where(p => p.Value is Tree && !skipped.Contains(p.Key) && !claims.Contains(p.Key))
                .OrderBy(p => ((Tree)p.Value).growthStage.Value >= Tree.treeStage ? 0 : 1)
                .ThenBy(p => Steps.Dist(Steps.ToPoint(p.Key), centre))
                .Select(p => p.Key)
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;

            var tile = next.Value;
            PlayerState.SetClaim(tile);
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(Steps.ToPoint(tile), walk);
            if (!walk.Ok)
            {
                skipped.Add(tile);
                continue;
            }
            // A full tree takes ~10 hits to fall, then a few more for the stump.
            for (int swing = 0; swing < 40 && farm.terrainFeatures.TryGetValue(tile, out var tf) && tf is Tree; swing++)
            {
                yield return Steps.Swing(axe);
                if (((Tree)tf).falling.Value)
                    yield return Wait.Seconds(2);
            }
            if (farm.terrainFeatures.ContainsKey(tile))
            {
                skipped.Add(tile);
                continue;
            }
            chopped++;
            // Wood drops around where the tree stood; walk over it to pick it up.
            yield return Wait.Seconds(1);
            yield return Steps.WalkTo(Steps.ToPoint(tile), new StepResult());
            yield return Wait.Seconds(1.5);
            job.Summary = $"Chopped down {chopped} tree{(chopped == 1 ? "" : "s")}; you now have {Me.Items.CountId("(O)388")} wood.";
        }
        if (chopped == 0)
            throw new JobFailed("no reachable trees on the farm");
        int gained = Me.Items.CountId("(O)388") - wood;
        job.Summary = $"Chopped down {chopped} tree{(chopped == 1 ? "" : "s")} (+{gained} wood, you have {Me.Items.CountId("(O)388")}).";
    }

    // ================================================================ craft

    /// <summary>Craft something you know the recipe for. Placeable things (scarecrow, chest, ...) get placed by the field.</summary>
    public static IEnumerator Craft(Job job, string name)
    {
        var known = Me.craftingRecipes.Keys.ToList();
        var recipeName = known.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new JobFailed($"you don't know how to craft '{name}'. You know: {string.Join(", ", known)}");
        var recipe = new CraftingRecipe(recipeName);
        if (!recipe.doesFarmerHaveIngredientsInInventory())
            throw new JobFailed($"not enough materials for {recipe.DisplayName}: needs {Ingredients(recipe)}");

        recipe.consumeIngredients(null);
        var item = recipe.createItem();
        if (!Me.addItemToInventoryBool(item))
            throw new JobFailed("your inventory is full");
        Game1.playSound("coin");
        job.Summary = $"Crafted {item.DisplayName}.";

        if (item is not SObject obj || !obj.isPlaceable() || obj.Category == SObject.SeedsCategory || obj.Edibility > 0)
            yield break;

        // Place it on the farm, next to the field.
        if (Me.currentLocation is not Farm)
        {
            var r = new StepResult();
            yield return Steps.GoToLocation("Farm", r);
            if (!r.Ok)
                yield break;
        }
        var farm = Game1.getFarm();
        var centre = FarmJobs.FieldCentre();
        var placeable = Me.Items.FirstOrDefault(i => i?.QualifiedItemId == item.QualifiedItemId) as SObject;
        if (placeable == null)
            yield break;
        var spots = Enumerable.Range(-10, 21).SelectMany(dx => Enumerable.Range(-10, 21).Select(dy => new Vector2(centre.X + dx, centre.Y + dy)))
            .Where(t => !farm.terrainFeatures.ContainsKey(t) && !farm.objects.ContainsKey(t) && Steps.IsStandable(farm, Steps.ToPoint(t))
                && placeable.canBePlacedHere(farm, t))
            .OrderBy(t => Vector2.DistanceSquared(t, new Vector2(centre.X, centre.Y)))
            .Take(5)
            .ToList();
        foreach (var spot in spots)
        {
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(Steps.ToPoint(spot), walk);
            if (!walk.Ok)
                continue;
            Steps.Equip(placeable);
            yield return null;
            if (placeable.placementAction(farm, (int)spot.X * 64, (int)spot.Y * 64, Me))
            {
                Me.reduceActiveItemByOne();
                job.Summary = $"Crafted a {item.DisplayName} and placed it by the field.";
                yield break;
            }
        }
        job.Summary = $"Crafted {item.DisplayName} (it's in your inventory; couldn't find a spot to place it).";
    }

    private static string Ingredients(CraftingRecipe recipe) =>
        string.Join(", ", recipe.recipeList.Select(i => $"{i.Value} {ItemRegistry.GetDataOrErrorItem(i.Key).DisplayName}"));

    /// <summary>Recipes the player knows, with whether they can craft them now (for the observation).</summary>
    public static List<string> Recipes()
    {
        var result = new List<string>();
        foreach (var name in Me.craftingRecipes.Keys)
        {
            try
            {
                var recipe = new CraftingRecipe(name);
                if (recipe.doesFarmerHaveIngredientsInInventory())
                    result.Add($"{recipe.DisplayName} (can craft now)");
                else if (name is "Scarecrow" or "Chest" or "Basic Fertilizer" or "Sprinkler")
                    result.Add($"{recipe.DisplayName} (needs {Ingredients(recipe)})");
            }
            catch (Exception)
            {
                // bad recipe data; ignore
            }
        }
        return result;
    }

    // ================================================================ shared chest

    /// <summary>Put items into the nearest chest on the farm (default: everything but tools and seeds).</summary>
    public static IEnumerator Store(Job job, string? itemName)
    {
        var chest = new Chest?[1];
        yield return OpenChest(chest);
        var c = chest[0]!;

        var moved = new List<string>();
        foreach (var item in Me.Items.Where(i => i != null).ToList())
        {
            bool wanted = itemName != null
                ? item.DisplayName.Contains(itemName, StringComparison.OrdinalIgnoreCase)
                : item is not Tool && item.Category != SObject.SeedsCategory;
            if (!wanted)
                continue;
            int stack = item.Stack;
            var left = c.addItem(item);
            int stored = stack - (left?.Stack ?? 0);
            if (stored <= 0)
                continue;
            if (left == null)
                Me.removeItemFromInventory(item);
            moved.Add($"{stored} {item.DisplayName}");
        }
        c.mutex.ReleaseLock();
        job.Summary = moved.Count == 0 ? "Nothing to store." : $"Stored {string.Join(", ", moved)} in the chest.";
    }

    /// <summary>Take an item out of the chest(s) on the farm.</summary>
    public static IEnumerator Take(Job job, string itemName, int count)
    {
        var chest = new Chest?[1];
        yield return OpenChest(chest, itemName);
        var c = chest[0]!;
        var items = c.GetItemsForPlayer();
        var item = items.FirstOrDefault(i => i != null && i.DisplayName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => i != null && i.DisplayName.Contains(itemName, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            c.mutex.ReleaseLock();
            throw new JobFailed($"there's no '{itemName}' in the chest. It has: {ChestContents(c)}");
        }
        int n = Math.Min(count, item.Stack);
        var taken = item.getOne();
        taken.Stack = n;
        if (!Me.couldInventoryAcceptThisItem(taken))
        {
            c.mutex.ReleaseLock();
            throw new JobFailed("your inventory is full");
        }
        Me.addItemToInventoryBool(taken);
        if (item.ConsumeStack(n) == null)
            items.Remove(item);
        c.clearNulls();
        c.mutex.ReleaseLock();
        job.Summary = $"Took {n} {taken.DisplayName} from the chest.";
    }

    /// <summary>Walk to a chest on the farm (preferring one containing <paramref name="containing"/>) and lock it.</summary>
    private static IEnumerator OpenChest(Chest?[] result, string? containing = null)
    {
        if (Me.currentLocation is not Farm)
        {
            var r = new StepResult();
            yield return Steps.GoToLocation("Farm", r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? "couldn't get to the farm");
        }
        var farm = Game1.getFarm();
        var chests = farm.objects.Pairs.Where(p => p.Value is Chest { playerChest.Value: true }).ToList();
        if (chests.Count == 0)
            throw new JobFailed("there's no chest on the farm yet - craft one (50 wood)");
        var pick = chests
            .OrderBy(p => containing != null && ((Chest)p.Value).GetItemsForPlayer().Any(i => i != null && i.DisplayName.Contains(containing, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(p => Steps.Dist(Steps.ToPoint(p.Key), Me.TilePoint))
            .First();
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(Steps.ToPoint(pick.Key), walk);
        if (!walk.Ok)
            throw new JobFailed("couldn't reach the chest");

        var chest = (Chest)pick.Value;
        bool? locked = null;
        chest.mutex.RequestLock(() => locked = true, () => locked = false);
        for (int i = 0; i < 300 && locked == null; i++)
            yield return null;
        if (locked != true)
            throw new JobFailed("someone else is using the chest");
        result[0] = chest;
    }

    private static string ChestContents(Chest c)
    {
        var items = c.GetItemsForPlayer().Where(i => i != null).Select(i => i.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName).ToList();
        return items.Count == 0 ? "nothing" : string.Join(", ", items);
    }

    /// <summary>What's in the farm's chests (for the observation).</summary>
    public static List<string> FarmChestContents() =>
        Game1.getFarm().objects.Values.OfType<Chest>().Where(c => c.playerChest.Value).Select(ChestContents).ToList();

    // ================================================================ talk / give

    /// <summary>Walk to a villager and talk; the villager's actual dialogue is reported back.</summary>
    public static IEnumerator TalkTo(Job job, string person, string? giftName)
    {
        SObject? gift = null;
        if (giftName != null)
        {
            gift = Me.Items.OfType<SObject>().FirstOrDefault(o => o.DisplayName.Equals(giftName, StringComparison.OrdinalIgnoreCase))
                ?? Me.Items.OfType<SObject>().FirstOrDefault(o => o.DisplayName.Contains(giftName, StringComparison.OrdinalIgnoreCase));
            // "forage", "a gift", "flowers": use the cheapest forage item we have.
            if (gift == null && giftName.ToLowerInvariant() is var g && (g.Contains("forage") || g.Contains("gift") || g.Contains("flower") || g.Contains("something")))
                gift = Me.Items.OfType<SObject>().Where(o => o.isForage() && o.canBeGivenAsGift()).OrderBy(o => o.sellToStorePrice()).FirstOrDefault();
            if (gift == null)
                throw new JobFailed($"you don't have '{giftName}' to give");
            if (!gift.canBeGivenAsGift())
                throw new JobFailed($"{gift.DisplayName} can't be given as a gift");
        }

        yield return FarmJobs.GoTo(job, person);
        job.Summary = "";
        var npc = Me.currentLocation.characters.FirstOrDefault(n => n.Name.Equals(person, StringComparison.OrdinalIgnoreCase)
                                                                 || n.displayName.Equals(person, StringComparison.OrdinalIgnoreCase))
            ?? throw new JobFailed($"{person} isn't a villager you can talk to here");
        int heartsBefore = Me.getFriendshipHeartLevelForNPC(npc.Name);

        if (gift != null)
            Steps.Equip(gift);
        else
            EquipTool();
        yield return null;
        Me.faceGeneralDirection(npc.Position, 0, false, false);
        if (!npc.checkAction(Me, Me.currentLocation))
            throw new JobFailed($"{npc.displayName} didn't respond (maybe busy or already talked to today)");

        // Read the dialogue box page by page, clicking through it.
        var lines = new List<string>();
        for (int i = 0; i < 600 && Game1.activeClickableMenu is DialogueBox; i++)
        {
            var box = (DialogueBox)Game1.activeClickableMenu;
            if (i % 45 == 0)
            {
                var text = box.getCurrentString();
                if (!string.IsNullOrWhiteSpace(text) && (lines.Count == 0 || lines[^1] != text))
                    lines.Add(text.Trim());
                if (box.isQuestion && box.responses.Length > 0)
                {
                    box.selectedResponse = 0; // just pick the first answer
                    lines.Add($"(you answered: {box.responses[0].responseText})");
                }
                box.safetyTimer = 0;
                box.receiveLeftClick(0, 0);
            }
            yield return null;
        }
        int heartsAfter = Me.getFriendshipHeartLevelForNPC(npc.Name);
        string said = lines.Count > 0 ? string.Join(" ", lines) : "(nothing)";
        if (said.Length > 300)
            said = said[..300] + "…";
        job.Summary = gift != null
            ? $"You gave {npc.displayName} a {gift.DisplayName}. {npc.displayName}: \"{said}\""
            : $"{npc.displayName} says: \"{said}\"";
        job.Summary += $" (Friendship: {heartsAfter} heart{(heartsAfter == 1 ? "" : "s")}{(heartsAfter > heartsBefore ? ", up!" : "")})";
    }

    private static void EquipTool()
    {
        if (Me.Items.OfType<Tool>().FirstOrDefault() is { } tool)
            Steps.Equip(tool);
    }
}
