using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>
/// Thin, one-tile player actions. Nothing here decides 'what the tool should do' - a swing is a
/// swing, a click is a click, and the game's own code (performToolAction / checkAction /
/// placementAction) decides what happens, exactly as if the player had done it. The job only
/// reports what changed.
/// </summary>
internal static class TileJobs
{
    private static Farmer Me => Game1.player;

    // ---------------------------------------------------------------- movement

    /// <summary>Walk to an exact tile of the current map.</summary>
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

    // ---------------------------------------------------------------- interact (right-click)

    /// <summary>
    /// The game's right-click on a tile: talk to villagers, open doors/chests/machines/counters,
    /// harvest ripe crops, pick up forage... Whatever the vanilla game would do.
    /// </summary>
    public static IEnumerator Use(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        // Right-click a villager: they react (dialogue opens; the agent reads and answers it).
        var npc = loc.characters.FirstOrDefault(n => n.TilePoint == tile && n.IsVillager);
        if (npc != null)
        {
            Me.faceGeneralDirection(npc.Position, 0, false, false);
            if (!npc.checkAction(Me, loc))
                throw new JobFailed($"{npc.displayName} didn't respond (busy or already talked to today?)");
            job.Summary = $"Talked to {npc.displayName} - the dialogue is up; read and answer it.";
            yield break;
        }

        // Ripe crops (and wild onions): harvest by right-clicking, like a player.
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is StardewValley.TerrainFeatures.HoeDirt { crop: not null } dirt
            && !dirt.crop.dead.Value)
        {
            if (Me.freeSpotsInInventory() == 0)
                throw new JobFailed("your inventory is full - ship or store something first");
            string name = Observer.CropName(dirt.crop);
            bool wasReady = dirt.readyForHarvest();
            dirt.performUseAction(v);
            yield return Wait.Ticks(30);
            if (!wasReady)
                throw new JobFailed($"the {name} at {tile.X},{tile.Y} isn't ripe yet - it needs water and days");
            job.Summary = $"Harvested the {name} at {tile.X},{tile.Y}.";
            yield break;
        }

        // The shipping bin also only opens on a literal right-click; build the same menu the game
        // builds (ShipItem behavior included), so the agent gets the real bin screen to click.
        if (loc.getBuildingAt(new Vector2(tile.X, tile.Y)) is StardewValley.Buildings.ShippingBin bin && bin.daysOfConstructionLeft.Value <= 0)
        {
            var ship = NobodyValley.MenuAgent.ShipBehavior(bin);
            var grab = new StardewValley.Menus.ItemGrabMenu(null, reverseGrab: true, showReceivingMenu: false,
                Utility.highlightShippableObjects, ship, "", null, snapToBottom: true, canBeExitedWithKey: true,
                playRightClickSound: false, allowRightClick: true, showOrganizeButton: false, 0, null, -1, bin);
            grab.initializeUpperRightCloseButton();
            grab.setBackgroundTransparency(b: false);
            grab.setDestroyItemOnClick(b: true);
            grab.initializeShippingBin();
            Game1.activeClickableMenu = grab;
            job.Summary = "It opened the shipping bin - read it and click.";
            yield break;
        }

        // A player chest only opens when the game sees the player "just right-clicked", so do the
        // same thing the click does under the hood: take the chest's mutex and ShowMenu()
        // (the game's own real chest menu).
        if (loc.objects.TryGetValue(v, out var obj) && obj is StardewValley.Objects.Chest { playerChest.Value: true } chest)
        {
            chest.GetMutex().RequestLock(chest.ShowMenu);
            yield return Wait.Ticks(5);
            if (Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu)
            {
                job.Summary = $"It opened the chest at {tile.X},{tile.Y} - read it and click.";
                yield break;
            }
            throw new JobFailed("couldn't open the chest (is someone else using it?)");
        }

        var before = Me.currentLocation;
        bool did = loc.checkAction(new xTile.Dimensions.Location(tile.X, tile.Y), Game1.viewport, Me);
        yield return Wait.Seconds(1);
        job.Summary = Me.currentLocation != before
            ? $"You went through to {Observer.LocationName(Me.currentLocation)}."
            : Game1.activeClickableMenu != null
                ? "It opened a menu - read it and click/answer."
                : did ? "Done." : "Nothing happened.";
    }

    // ---------------------------------------------------------------- use the held item (left-click)

    /// <summary>Left-click with the held item: plant seeds, place craftables/furniture, ship things
    /// into the bin... (The placement is the game's own placementAction.)</summary>
    public static IEnumerator Place(Job job, Point tile)
    {
        var loc = Me.currentLocation;
        var v = new Vector2(tile.X, tile.Y);
        var item = Me.CurrentItem;
        if (item == null)
            throw new JobFailed("your hand is empty - equip an item first (equip \"Parsnip Seeds\")");

        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        // Left-click the shipping bin while holding something shippable: the vanilla ship action
        // (sold overnight; the bin's menu is just an overview).
        if (loc.getBuildingAt(v) is StardewValley.Buildings.ShippingBin bin && bin.daysOfConstructionLeft.Value <= 0)
        {
            if (item is not SObject shippable || !shippable.canBeShipped())
                throw new JobFailed($"{item.DisplayName} can't be shipped");
            Me.ActiveItem = shippable;
            if (bin.leftClicked())
                job.Summary = $"Put {shippable.DisplayName} in the shipping bin - sold overnight.";
            else
                throw new JobFailed("couldn't put it in the bin (stand closer?)");
            yield break;
        }

        if (item is Tool)
            throw new JobFailed($"you're holding a tool ({item.DisplayName}) - use its own action (axe/pickaxe/hoe/...) instead");
        if (item is not SObject obj)
            throw new JobFailed($"you can't place {item.DisplayName} like that");

        bool wasPlaceable = obj.canBePlacedHere(loc, v);
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        int before = Me.Items.CountId(obj.QualifiedItemId);
        if (obj.placementAction(loc, (int)v.X * 64, (int)v.Y * 64, Me))
        {
            Me.reduceActiveItemByOne(); // what the game does after a successful placement
            int used = before - Me.Items.CountId(obj.QualifiedItemId);
            job.Summary = used > 0
                ? $"Placed {used} {obj.DisplayName} at {tile.X},{tile.Y}."
                : $"Used the {obj.DisplayName} at {tile.X},{tile.Y}.";
        }
        else
        {
            throw new JobFailed($"can't place {obj.DisplayName} there{(!wasPlaceable ? " (it doesn't fit that ground)" : " - wrong season or occupied?")}");
        }
    }

    // ---------------------------------------------------------------- swing a tool (left-click with tool)

    /// <summary>One swing of a specific tool at a tile - the vanilla use-tool action for that tile,
    /// nothing more. The game decides the outcome (tree, rock, grass, crop, soil, monster, ...).</summary>
    public static IEnumerator Swing(Job job, Point tile, string toolName)
    {
        Tool? tool = toolName.ToLowerInvariant() switch
        {
            "axe" => Steps.FindTool<Axe>(),
            "pickaxe" => Steps.FindTool<Pickaxe>(),
            "hoe" => Steps.FindTool<Hoe>(),
            "watering_can" or "watering can" or "can" => Steps.FindTool<WateringCan>(),
            "scythe" => Steps.FindScythe(),
            _ => null,
        };
        if (tool == null)
            throw new JobFailed(toolName == "scythe" ? "you don't have a scythe" : $"you don't have a {toolName}");
        var what = Describe(Me.currentLocation, new Vector2(tile.X, tile.Y));
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(tile, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach {tile.X},{tile.Y}");

        yield return Steps.Swing(tool);
        job.Summary = what != null
            ? $"Swung the {toolName} at the {what} at {tile.X},{tile.Y}."
            : $"Swung the {toolName} at {tile.X},{tile.Y}.";
    }

    /// <summary>What's on a tile, read generically (for the swing summary).</summary>
    public static string? Describe(GameLocation loc, Vector2 v)
    {
        if (loc.resourceClumps.FirstOrDefault(c => c.occupiesTile((int)v.X, (int)v.Y)) is { } clump)
            return clump.parentSheetIndex.Value is 600 or 602 ? "stump" : "boulder";
        if (loc.objects.TryGetValue(v, out var o) && (o.IsWeeds() || o.IsTwig() || o.IsBreakableStone() || (o.IsSpawnedObject && o.isForage())))
            return o.DisplayName.ToLowerInvariant();
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
            return tf switch
            {
                StardewValley.TerrainFeatures.Tree => "tree",
                StardewValley.TerrainFeatures.Grass => "grass",
                StardewValley.TerrainFeatures.HoeDirt { crop: null } => "tilled soil",
                StardewValley.TerrainFeatures.HoeDirt { crop: { } c } when c.dead.Value => "dead crop",
                StardewValley.TerrainFeatures.HoeDirt { crop: { } c } => $"growing {Observer.CropName(c)}",
                _ => null,
            };
        return null;
    }
}