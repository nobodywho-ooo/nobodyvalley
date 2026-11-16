using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Crops;
using StardewValley.Internal;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>The high-level actions exposed to agents. Each returns a coroutine body for <see cref="Job"/>.</summary>
internal static class FarmJobs
{
    private static ModConfig Config => ModEntry.Instance.Config;
    private static Farmer Me => Game1.player;

    /// <summary>Where the shared field is centred.</summary>
    public static Point FieldCentre()
    {
        var door = Game1.getFarm().GetMainFarmHouseEntry();
        return new Point(door.X + Config.FieldOffsetX, door.Y + Config.FieldOffsetY);
    }

    // ================================================================ go_to

    public static IEnumerator GoTo(Job job, string target)
    {
        var resolved = Resolve(target) ?? throw new JobFailed(
            Game1.characterData.Keys.FirstOrDefault(k => k.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase)) is { } villager
                ? $"you don't know where {villager} is right now - try their home, the town or the saloon"
                : $"unknown place or person '{target}'");
        if (resolved.Person == null && Me.currentLocation.NameOrUniqueName == Navigation.GetLocation(resolved.Location)?.NameOrUniqueName)
        {
            // Agents that can't see what they're looking for tend to go_to where they already are, in a loop.
            job.Summary = $"You're already at {Observer.LocationName(Me.currentLocation)}; nothing changed. Use walk x,y, "
                + "or a bulk action (e.g. harvest, water) that finds things by itself.";
            yield break;
        }
        var r = new StepResult();
        yield return Steps.GoToLocation(resolved.Location, r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get there");

        if (resolved.Person != null)
        {
            // People move around; re-target a few times.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var who = resolved.Person();
                if (who == null || who.currentLocation != Me.currentLocation)
                    throw new JobFailed($"{target} isn't here any more");
                if (Steps.Dist(who.TilePoint, Me.TilePoint) <= 2)
                    break;
                var adj = new StepResult();
                yield return Steps.WalkAdjacent(who.TilePoint, adj);
            }
            var person = resolved.Person();
            int dist = person == null ? 99 : Steps.Dist(person.TilePoint, Me.TilePoint);
            if (dist > 2)
                throw new JobFailed($"you're in {Observer.LocationName(Me.currentLocation)} but couldn't reach {target} ({dist} tiles away)");
            Me.faceGeneralDirection(person!.Position, 0, false, false);
            job.Summary = $"You are next to {target} in {Observer.LocationName(Me.currentLocation)}.";
        }
        else
        {
            job.Summary = $"You arrived at {Observer.LocationName(Me.currentLocation)}.";
        }
    }

    private sealed record Target(string Location, Func<Character?>? Person);

    private static Target? Resolve(string target)
    {
        var q = target.Trim();
        if (q.Equals("home", StringComparison.OrdinalIgnoreCase) || q.Equals("bed", StringComparison.OrdinalIgnoreCase)
            || q.Equals("house", StringComparison.OrdinalIgnoreCase))
            return new Target(Utility.getHomeOfFarmer(Me).NameOrUniqueName, null);

        if (q.Equals("field", StringComparison.OrdinalIgnoreCase) || q.Equals("crops", StringComparison.OrdinalIgnoreCase))
            return new Target("Farm", null);

        foreach (var f in Game1.getOnlineFarmers())
        {
            if (f.Name.Equals(q, StringComparison.OrdinalIgnoreCase) && f.UniqueMultiplayerID != Me.UniqueMultiplayerID && f.currentLocation != null)
            {
                long id = f.UniqueMultiplayerID;
                return new Target(f.currentLocation.NameOrUniqueName, () => Game1.getOnlineFarmers().FirstOrDefault(x => x.UniqueMultiplayerID == id));
            }
        }

        var npc = Game1.getCharacterFromName(q);
        if (npc?.currentLocation != null)
            return new Target(npc.currentLocation.NameOrUniqueName, () => Game1.getCharacterFromName(q));

        if (Places.Find(q) is { } place)
            return new Target(place.Location, null);

        if (Navigation.GetLocation(q) is { } loc)
            return new Target(loc.NameOrUniqueName, null);

        return null;
    }

    // ================================================================ clear debris

    public static IEnumerator ClearDebris(Job job, int count)
    {
        yield return EnsureOnFarm();
        var farm = Game1.getFarm();
        var centre = FieldCentre();
        var skipped = new HashSet<Vector2>();
        int cleared = 0;

        while (cleared < count)
        {
            var claims = PlayerState.OthersClaims();
            var next = farm.objects.Pairs
                .Where(p => IsClearable(p.Value) && !skipped.Contains(p.Key) && !claims.Contains(p.Key))
                .Select(p => p.Key)
                .OrderBy(t => Steps.Dist(Steps.ToPoint(t), centre))
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;

            var tile = next.Value;
            PlayerState.SetClaim(tile);
            var r = new StepResult();
            yield return ClearTile(tile, r);
            if (r.Ok)
            {
                cleared++;
                job.Summary = $"Cleared {cleared} of {count} debris.";
            }
            else
            {
                skipped.Add(tile);
            }
        }
        if (cleared == 0)
            throw new JobFailed("no reachable debris (weeds, stones, twigs) near the field");
        job.Summary = $"Cleared {cleared} debris near the field.";
    }

    private static bool IsClearable(SObject o) => o.IsWeeds() || o.IsBreakableStone() || o.IsTwig();

    /// <summary>Remove a weed/stone/twig/grass on a tile with the right tool.</summary>
    private static IEnumerator ClearTile(Vector2 tile, StepResult r)
    {
        var loc = Me.currentLocation;
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(Steps.ToPoint(tile), walk);
        if (!walk.Ok)
        {
            r.Fail(walk.Error ?? "unreachable");
            yield break;
        }

        for (int swings = 0; swings < 8; swings++)
        {
            Tool? tool = null;
            if (loc.objects.TryGetValue(tile, out var obj))
            {
                if (obj.IsWeeds())
                    tool = Steps.FindScythe();
                else if (obj.IsBreakableStone())
                    tool = Steps.FindTool<Pickaxe>();
                else if (obj.IsTwig())
                    tool = Steps.FindTool<Axe>();
                else
                {
                    r.Fail($"{obj.DisplayName} is in the way");
                    yield break;
                }
            }
            else if (loc.terrainFeatures.TryGetValue(tile, out var tf) && tf is Grass)
            {
                tool = Steps.FindScythe();
            }
            else
            {
                r.Ok = true;
                yield break;
            }

            if (tool == null)
            {
                r.Fail("missing the right tool");
                yield break;
            }
            yield return Steps.Swing(tool);
        }
        r.Fail("it won't break");
    }

    // ================================================================ till

    public static IEnumerator Till(Job job, int count)
    {
        var hoe = Steps.FindTool<Hoe>() ?? throw new JobFailed("you don't have a hoe");
        yield return EnsureOnFarm();
        var farm = Game1.getFarm();
        var centre = FieldCentre();
        var skipped = new HashSet<Vector2>();
        int tilled = 0;

        while (tilled < count)
        {
            var claims = PlayerState.OthersClaims();
            Vector2? next = FieldTiles(farm, centre, 12)
                .Where(t => !skipped.Contains(t) && !claims.Contains(t) && CanTill(farm, t))
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;
            var tile = next.Value;
            PlayerState.SetClaim(tile);

            var clear = new StepResult();
            yield return ClearTile(tile, clear);
            if (!clear.Ok)
            {
                skipped.Add(tile);
                continue;
            }
            yield return Steps.Swing(hoe);

            if (farm.terrainFeatures.TryGetValue(tile, out var tf) && tf is HoeDirt)
            {
                tilled++;
                job.Summary = $"Tilled {tilled} of {count} tiles.";
            }
            else
            {
                skipped.Add(tile);
            }
        }
        if (tilled == 0)
            throw new JobFailed("couldn't find any tillable ground near the field");
        job.Summary = $"Tilled {tilled} tiles of soil in the field.";
    }

    /// <summary>Tiles around the field centre, nearest first (so the field grows as a compact blob).</summary>
    private static IEnumerable<Vector2> FieldTiles(Farm farm, Point centre, int maxRadius)
    {
        var tiles = new List<Vector2>();
        for (int y = centre.Y - maxRadius; y <= centre.Y + maxRadius; y++)
        for (int x = centre.X - maxRadius; x <= centre.X + maxRadius; x++)
        {
            var t = new Vector2(x, y);
            if (farm.isTileOnMap(t))
                tiles.Add(t);
        }
        return tiles.OrderBy(t => Vector2.DistanceSquared(t, new Vector2(centre.X, centre.Y))).ThenBy(t => t.Y).ThenBy(t => t.X);
    }

    private static bool CanTill(Farm farm, Vector2 t)
    {
        if (farm.doesTileHaveProperty((int)t.X, (int)t.Y, "Diggable", "Back") == null)
            return false;
        if (farm.terrainFeatures.TryGetValue(t, out var tf) && tf is not Grass)
            return false;
        if (farm.objects.TryGetValue(t, out var o) && !IsClearable(o))
            return false;
        if (farm.getBuildingAt(t) != null || farm.isWaterTile((int)t.X, (int)t.Y))
            return false;
        // Keep the farmhouse porch walkable.
        var door = farm.GetMainFarmHouseEntry();
        if (Math.Abs(t.X - door.X) <= 1 && t.Y >= door.Y && t.Y <= door.Y + 2)
            return false;
        return true;
    }

    // ================================================================ plant

    public static IEnumerator Plant(Job job, string? seedName, int count)
    {
        var seeds = FindSeeds(seedName) ?? throw new JobFailed(seedName == null
            ? "you have no seeds - buy some at Pierre's"
            : $"you have no '{seedName}'");
        string seedId = seeds.ItemId;
        string seedLabel = seeds.DisplayName;
        yield return EnsureOnFarm();
        var farm = Game1.getFarm();
        var centre = FieldCentre();
        var skipped = new HashSet<Vector2>();
        int planted = 0;

        while (planted < count)
        {
            seeds = Me.Items.OfType<SObject>().FirstOrDefault(i => i.ItemId == seedId);
            if (seeds == null)
                break;

            var claims = PlayerState.OthersClaims();
            Vector2? next = farm.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt { crop: null } && !skipped.Contains(p.Key) && !claims.Contains(p.Key))
                .Where(p => farm.CanPlantSeedsHere(seedId, (int)p.Key.X, (int)p.Key.Y, false, out _))
                .Select(p => p.Key)
                .OrderBy(t => Steps.Dist(Steps.ToPoint(t), centre))
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;

            var tile = next.Value;
            PlayerState.SetClaim(tile);
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(Steps.ToPoint(tile), walk);
            if (!walk.Ok || !farm.terrainFeatures.TryGetValue(tile, out var tf) || tf is not HoeDirt dirt || dirt.crop != null)
            {
                skipped.Add(tile);
                continue;
            }

            Steps.Equip(seeds);
            yield return null;
            if (dirt.plant(seedId, Me, isFertilizer: false))
            {
                Me.Items.ReduceId(seedId, 1);
                planted++;
                job.Summary = $"Planted {planted} {seedLabel}.";
            }
            else
            {
                skipped.Add(tile);
            }
            yield return Wait.Ticks(15);
        }
        if (planted == 0)
            throw new JobFailed("no empty tilled soil to plant in (till some first)");
        job.Summary = $"Planted {planted} {seedLabel}.";
    }

    public static SObject? FindSeeds(string? name)
    {
        var seeds = Me.Items.OfType<SObject>().Where(o => o.Category == SObject.SeedsCategory).ToList();
        if (name == null)
            return seeds.FirstOrDefault();
        return seeds.FirstOrDefault(s => s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? seeds.FirstOrDefault(s => s.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(s.DisplayName.Replace(" Seeds", ""), StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================ water

    public static IEnumerator Water(Job job) => Water(job, int.MaxValue);

    public static IEnumerator Water(Job job, int max)
    {
        var can = Steps.FindTool<WateringCan>() ?? throw new JobFailed("you don't have a watering can");
        var farm = Game1.getFarm();
        // Check before walking all the way to the farm for nothing.
        if (farm.IsRainingHere())
        {
            job.Summary = "It's raining, so the crops are watered already.";
            yield break;
        }
        if (!farm.terrainFeatures.Values.OfType<HoeDirt>().Any(d => d.needsWatering() && !d.isWatered()))
        {
            job.Summary = "All crops are already watered.";
            yield break;
        }
        yield return EnsureOnFarm();

        var skipped = new HashSet<Vector2>();
        int watered = 0;
        while (watered < max)
        {
            var claims = PlayerState.OthersClaims();
            var here = Me.TilePoint;
            Vector2? next = farm.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt d && d.needsWatering() && !d.isWatered() && !skipped.Contains(p.Key) && !claims.Contains(p.Key))
                .Select(p => p.Key)
                .OrderBy(t => Steps.Dist(Steps.ToPoint(t), here))
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;

            if (can.WaterLeft <= 0)
            {
                var refill = new StepResult();
                yield return Refill(can, refill);
                if (!refill.Ok)
                    throw new JobFailed(refill.Error ?? "couldn't refill the watering can");
            }

            var tile = next.Value;
            PlayerState.SetClaim(tile);
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(Steps.ToPoint(tile), walk);
            if (!walk.Ok)
            {
                skipped.Add(tile);
                continue;
            }
            yield return Steps.Swing(can);
            if (farm.terrainFeatures.TryGetValue(tile, out var tf) && tf is HoeDirt d2 && d2.isWatered())
            {
                watered++;
                job.Summary = $"Watered {watered} crops.";
            }
            else
            {
                skipped.Add(tile);
            }
        }
        int left = farm.terrainFeatures.Values.OfType<HoeDirt>().Count(d => d.needsWatering() && !d.isWatered());
        job.Summary = watered == 0 && left == 0 ? "All crops are already watered."
            : left == 0 ? $"Watered {watered} crops; all crops are now watered."
            : $"Watered {watered} crops; {left} still need water.";
    }

    public static IEnumerator Refill(WateringCan can, StepResult r)
    {
        var loc = Me.currentLocation;
        var here = Me.TilePoint;
        var water = WaterTiles(loc).OrderBy(t => Steps.Dist(t, here)).Take(8).ToList();
        foreach (var w in water)
        {
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(w, walk);
            if (!walk.Ok)
                continue;
            yield return Steps.Swing(can);
            if (can.WaterLeft > 0)
            {
                r.Ok = true;
                yield break;
            }
        }
        r.Fail("no reachable water to refill the watering can");
    }

    private static readonly Dictionary<string, List<Point>> waterCache = new();

    private static List<Point> WaterTiles(GameLocation loc)
    {
        if (waterCache.TryGetValue(loc.NameOrUniqueName, out var cached))
            return cached;
        var list = new List<Point>();
        var layer = loc.map.Layers[0];
        for (int x = 0; x < layer.LayerWidth; x++)
        for (int y = 0; y < layer.LayerHeight; y++)
        {
            if (!loc.CanRefillWateringCanOnTile(x, y))
                continue;
            // Only shore tiles are useful.
            if (Steps.IsStandable(loc, new Point(x + 1, y)) || Steps.IsStandable(loc, new Point(x - 1, y))
                || Steps.IsStandable(loc, new Point(x, y + 1)) || Steps.IsStandable(loc, new Point(x, y - 1)))
                list.Add(new Point(x, y));
        }
        waterCache[loc.NameOrUniqueName] = list;
        return list;
    }

    // ================================================================ harvest

    public static IEnumerator Harvest(Job job) => Harvest(job, int.MaxValue);

    public static IEnumerator Harvest(Job job, int max)
    {
        var farm = Game1.getFarm();
        if (!farm.terrainFeatures.Values.OfType<HoeDirt>().Any(d => d.readyForHarvest()))
        {
            job.Summary = "Nothing is ready to harvest on the farm.";
            yield break;
        }
        yield return EnsureOnFarm();
        var skipped = new HashSet<Vector2>();
        var counts = new Dictionary<string, int>();

        while (counts.Values.Sum() < max)
        {
            var claims = PlayerState.OthersClaims();
            var here = Me.TilePoint;
            Vector2? next = farm.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt d && d.readyForHarvest() && !skipped.Contains(p.Key) && !claims.Contains(p.Key))
                .Select(p => p.Key)
                .OrderBy(t => Steps.Dist(Steps.ToPoint(t), here))
                .Cast<Vector2?>()
                .FirstOrDefault();
            if (next == null)
                break;
            if (Me.freeSpotsInInventory() == 0)
                throw new JobFailed("your inventory is full - ship something first");

            var tile = next.Value;
            PlayerState.SetClaim(tile);
            var walk = new StepResult();
            yield return Steps.WalkAdjacent(Steps.ToPoint(tile), walk);
            if (!walk.Ok || !farm.terrainFeatures.TryGetValue(tile, out var tf) || tf is not HoeDirt dirt || dirt.crop == null)
            {
                skipped.Add(tile);
                continue;
            }

            string name = Observer.CropName(dirt.crop);
            if (dirt.crop.GetHarvestMethod() == HarvestMethod.Scythe && Steps.FindScythe() is { } scythe)
                Steps.Equip(scythe);
            dirt.performUseAction(tile);
            yield return Wait.Ticks(30);

            if (!dirt.readyForHarvest() || dirt.crop == null)
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
                job.Summary = "Harvested " + string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"));
            }
            else
            {
                skipped.Add(tile);
            }
        }
        if (counts.Count == 0)
            job.Summary = "Nothing is ready to harvest.";
        else
            job.Summary += ".";
    }

    // ================================================================ ship

    public static IEnumerator Ship(Job job, string? itemName)
    {
        var items = Me.Items.Where(i => i != null && IsShippable(i, itemName)).ToList();
        if (items.Count == 0)
            throw new JobFailed(itemName == null ? "you have no crops or forage to ship" : $"you have no '{itemName}'");

        yield return EnsureOnFarm();
        var farm = Game1.getFarm();
        var bin = farm.buildings.OfType<ShippingBin>().FirstOrDefault() ?? throw new JobFailed("there's no shipping bin");
        var r = new StepResult();
        yield return Steps.WalkTo(new Point(bin.tileX.Value, bin.tileY.Value + 1), r, Steps.Up);
        if (!r.Ok)
            yield return Steps.WalkTo(new Point(bin.tileX.Value + 1, bin.tileY.Value + 1), r, Steps.Up);
        if (!r.Ok)
            throw new JobFailed("can't reach the shipping bin");

        int total = 0, value = 0;
        var names = new List<string>();
        foreach (var item in Me.Items.Where(i => i != null && IsShippable(i, itemName)).ToList())
        {
            total += item.Stack;
            value += item.sellToStorePrice() * item.Stack;
            names.Add($"{item.Stack} {item.DisplayName}");
            farm.shipItem(item, Me);
            yield return Wait.Ticks(15);
        }
        job.Summary = $"Shipped {string.Join(", ", names)} (worth about {value}g, paid out overnight).";
    }

    private static bool IsShippable(Item i, string? name)
    {
        if (name != null)
            return i.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) && i.canBeShipped();
        // Crops, forage (incl. beach finds like clams, which count as fish), fish and animal products;
        // not materials (wood, stone, ...) that are needed for crafting.
        return i is SObject o && i.canBeShipped() && (o.isForage() || o.Category is SObject.VegetableCategory
            or SObject.FruitsCategory or SObject.flowersCategory or SObject.GreensCategory or SObject.FishCategory
            or SObject.EggCategory or SObject.MilkCategory or SObject.artisanGoodsCategory or SObject.sellAtPierresAndMarnies);
    }

    // ================================================================ buy

    public static IEnumerator Buy(Job job, string itemName, int count)
    {
        count = Math.Clamp(count, 1, 99);
        // Which shop sells it? (Pierre's: seeds etc., Willy's: rods, bait, tackle.)
        var shops = new[] { (id: "SeedShop", owner: "Pierre", counter: "Buy General", name: "Pierre's"), (id: "FishShop", owner: "Willy", counter: "Buy Fish", name: "Willy's") };
        KeyValuePair<ISalable, ItemStockInformation> match = default;
        var shopInfo = shops[0];
        foreach (var s in shops)
        {
            var stockHere = ShopBuilder.GetShopStock(s.id);
            match = stockHere.FirstOrDefault(x => x.Key.DisplayName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null)
                match = stockHere.FirstOrDefault(x => x.Key.DisplayName.Contains(itemName, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null)
            {
                shopInfo = s;
                break;
            }
        }
        if (match.Key == null)
            throw new JobFailed($"neither Pierre nor Willy sells '{itemName}' today");

        var r = new StepResult();
        yield return Steps.GoToLocation(shopInfo.id, r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? $"couldn't get to {shopInfo.name}");

        var shop = Me.currentLocation;
        var counter = FindActionTile(shop, shopInfo.counter) ?? FindActionTile(shop, $"OpenShop {shopInfo.id}");
        if (counter != null)
        {
            var stand = Navigation.ClosestStandable(shop, new Point(counter.Value.X, counter.Value.Y + 1), 2);
            if (stand != null)
                yield return Steps.WalkTo(stand.Value, new StepResult(), Steps.Up);
        }

        if (shop.getCharacterFromName(shopInfo.owner) == null)
            throw new JobFailed($"{shopInfo.owner} isn't at the counter (shops are open about 9am-5pm)");

        var salable = match.Key;
        int price = match.Value.Price;
        count = Math.Min(count, match.Value.Stock);
        int affordable = price > 0 ? Me.Money / price : count;
        if (affordable <= 0)
            throw new JobFailed($"{salable.DisplayName} costs {price}g but you only have {Me.Money}g");
        count = Math.Min(count, affordable);

        var item = ItemRegistry.Create(salable.QualifiedItemId, count);
        if (!Me.couldInventoryAcceptThisItem(item))
            throw new JobFailed("your inventory is full");
        Me.Money -= price * count;
        Me.addItemToInventoryBool(item);
        Game1.playSound("purchaseClick");
        job.Summary = $"Bought {count} {salable.DisplayName} for {price * count}g. Money left: {Me.Money}g.";
    }

    public static Point? FindActionTile(GameLocation loc, string actionPrefix, bool exact = false)
    {
        var layer = loc.map.GetLayer("Buildings");
        if (layer == null)
            return null;
        for (int x = 0; x < layer.LayerWidth; x++)
        for (int y = 0; y < layer.LayerHeight; y++)
        {
            var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
            if (action != null && (exact
                    ? action.Equals(actionPrefix, StringComparison.OrdinalIgnoreCase) || action.StartsWith(actionPrefix + " ", StringComparison.OrdinalIgnoreCase)
                    : action.StartsWith(actionPrefix, StringComparison.OrdinalIgnoreCase)))
                return new Point(x, y);
        }
        return null;
    }

    /// <summary>Seeds Pierre sells today, for the observation.</summary>
    public static List<string> SeedsForSale()
    {
        try
        {
            return ShopBuilder.GetShopStock("SeedShop")
                .Where(s => s.Key is SObject { Category: SObject.SeedsCategory })
                .Select(s => $"{s.Key.DisplayName} ({s.Value.Price}g)")
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    // ================================================================ eat

    public static IEnumerator Eat(Job job, string? itemName)
    {
        var food = Me.Items.OfType<SObject>()
            .Where(o => o.Edibility > 0 && (itemName == null || o.DisplayName.Contains(itemName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => o.sellToStorePrice())
            .FirstOrDefault() ?? throw new JobFailed(itemName == null ? "you have nothing edible" : $"you have no edible '{itemName}'");

        float before = Me.Stamina;
        Steps.Equip(food);
        yield return null;
        Me.mostRecentlyGrabbedItem = food;
        Me.eatHeldObject();
        yield return Wait.Seconds(3);
        job.Summary = $"Ate {food.DisplayName}. Energy {(int)before} -> {(int)Me.Stamina}.";
    }

    // ================================================================ sleep

    public static IEnumerator Sleep(Job job)
    {
        if (Game1.activeClickableMenu is ReadyCheckDialog || Me.isInBed.Value && Me.timeWentToBed.Value != 0)
        {
            job.Summary = "You're already in bed, waiting for the others to sleep.";
            yield break;
        }
        var home = Utility.getHomeOfFarmer(Me);
        var r = new StepResult();
        yield return Steps.GoToLocation(home.NameOrUniqueName, r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get home");

        // Walking onto the bed pops up the vanilla "Go to sleep for the night?" question; answer it
        // like a click on "Yes" would, so the normal (multiplayer-aware) sleep flow runs.
        var bed = home.GetPlayerBedSpot();
        yield return Steps.WalkTo(bed, new StepResult());
        for (int i = 0; i < 300; i++)
        {
            if (Game1.activeClickableMenu is DialogueBox { isQuestion: true } box && Me.currentLocation.lastQuestionKey == "Sleep")
            {
                int yes = Array.FindIndex(box.responses, r => r.responseKey == "Yes");
                if (yes < 0)
                    throw new JobFailed("couldn't answer the sleep question");
                box.safetyTimer = 0;
                box.selectedResponse = yes;
                box.receiveLeftClick(0, 0);
                job.Summary = "You're in bed. The day ends once everyone is asleep.";
                yield break;
            }
            if (Game1.activeClickableMenu == null && i == 60)
            {
                // No question appeared (e.g. we were already standing on the bed): nudge the bed action.
                Me.currentLocation.checkAction(new xTile.Dimensions.Location(bed.X, bed.Y), Game1.viewport, Me);
            }
            yield return null;
        }
        throw new JobFailed("got to the bed, but couldn't go to sleep");
    }

    // ================================================================ helpers

    private static IEnumerator EnsureOnFarm()
    {
        if (Me.currentLocation is Farm)
            yield break;
        var r = new StepResult();
        yield return Steps.GoToLocation("Farm", r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get to the farm");
    }
}
