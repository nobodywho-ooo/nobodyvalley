using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace NobodyValley;

/// <summary>
/// The agent's whole UI layer: real Stardew menus are serialized into text for the observation
/// ("what's on the screen"), and agent clicks are dispatched to the same menu code a player's
/// mouse click would hit. The mod understands nothing about menus - the agent reads them.
/// Dialogues, letters and questions are single-purpose pop-ups reported by ScreenMessages (the
/// agent answers with `answer`); shops, chests and level-ups are interactive menus handled here.
/// </summary>
internal static class MenuAgent
{
    internal static IReflectionHelper Reflection = null!;

    private static Farmer Me => Game1.player;

    /// <summary>Describe the current menu as the agent should see it, or null when none is open.</summary>
    public static string? Describe()
    {
        var m = Game1.activeClickableMenu;
        return m switch
        {
            ShopMenu shop => Shop(shop),
            ItemGrabMenu grab => Grab(grab),
            LevelUpMenu level => Level(level),
            _ => null, // dialogues/letters/ready checks are screen messages; the GameMenu isn't modelled yet
        };
    }

    /// <summary>The ShippingBin's private shipItem callback (what clicking a bin item invokes), as a delegate.</summary>
    public static ItemGrabMenu.behaviorOnItemSelect ShipBehavior(StardewValley.Buildings.ShippingBin bin)
    {
        var method = typeof(StardewValley.Buildings.ShippingBin).GetMethod("shipItem",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (ItemGrabMenu.behaviorOnItemSelect)method!.CreateDelegate(typeof(ItemGrabMenu.behaviorOnItemSelect), bin);
    }

    /// <summary>Click a named element of the current menu (exactly like clicking the on-screen button).</summary>
    public static string Click(string element, int n)
    {
        var m = Game1.activeClickableMenu;
        return m switch
        {
            ShopMenu shop => ShopClick(shop, element, n),
            ItemGrabMenu grab => GrabClick(grab, element),
            LevelUpMenu level => LevelClick(level, element),
            _ => $"there's no clickable menu open right now (clickable: shop/chest/bundle/level-up)",
        };
    }

    /// <summary>Close the current menu, as if the player pressed the close key.</summary>
    public static string Close()
    {
        var m = Game1.activeClickableMenu;
        if (m == null)
            return "there's no menu open";
        if (m is ItemGrabMenu { heldItem: not null })
            return "you're holding an item - put it somewhere (store it) or click=close again to drop it back";
        m.exitThisMenu(playSound: true);
        return "Closed the menu.";
    }

    // ---------------------------------------------------------------- shops

    private static string Shop(ShopMenu shop)
    {
        var sb = new StringBuilder();
        if (shop.heldItem != null)
            sb.Append($"(you're holding {shop.heldItem.DisplayName} x{shop.heldItem.Stack} on the cursor - it isn't in your backpack yet)");
        sb.Append($" Shop is open; you have {Me.Money}g. Click an item below to buy it (it's put into your backpack for you); click one of YOUR items (below/right) to sell it.");
        for (int k = 0; k < shop.forSaleButtons.Count; k++)
        {
            int idx = shop.currentItemIndex + k;
            if (idx >= shop.forSale.Count || shop.forSale[idx] == null)
                continue;
            var salable = shop.forSale[idx];
            int price = shop.itemPriceAndStock.TryGetValue(salable, out var info) ? info.Price : salable.salePrice();
            int stock = shop.itemPriceAndStock.TryGetValue(salable, out var info2) ? info2.Stock : -1;
            string stockText = stock <= 0 ? "" : stock >= int.MaxValue / 2 ? ", plenty in stock" : $", {stock} left";
            sb.Append($"\n- {salable.DisplayName} ({price}g{stockText})");
        }
        if (shop.forSale.Count > shop.forSaleButtons.Count)
            sb.Append("\n(more items - click=\"next\" to scroll)");
        sb.Append("\nActions: click=\"<item name>\" (buy/sell 1, n=N for N), click=\"next\"/\"prev\", click=\"close\".");
        return sb.ToString();
    }

    private static bool Matches(Item item, string name) =>
        item.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)
        || item.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static string ShopClick(ShopMenu shop, string element, int n)
    {
        string q = element.Trim();
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
            return Close();
        if (q.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            int before = shop.currentItemIndex;
            shop.receiveLeftClick(shop.downArrow.bounds.Center.X, shop.downArrow.bounds.Center.Y);
            return shop.currentItemIndex > before ? "Scrolled down." : "Already at the bottom.";
        }
        if (q.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            int before = shop.currentItemIndex;
            shop.receiveLeftClick(shop.upArrow.bounds.Center.X, shop.upArrow.bounds.Center.Y);
            return shop.currentItemIndex < before ? "Scrolled up." : "Already at the top.";
        }
        // Selling: click the item in our own inventory grid (the shop pays for it if it buys that).
        ClickableComponent? slot = shop.inventory.inventory.FirstOrDefault(s => s?.item != null && Matches(s.item, q));
        if (slot != null)
        {
            int moneyBeforeSell = Me.Money;
            int clicksSell = Math.Clamp(n, 1, 25);
            for (int i = 0; i < clicksSell; i++)
            {
                slot = shop.inventory.inventory.FirstOrDefault(s => s?.item != null && Matches(s.item, q));
                if (slot == null)
                    break;
                shop.receiveLeftClick(slot.bounds.Center.X, slot.bounds.Center.Y);
            }
            return Me.Money > moneyBeforeSell
                ? $"Sold {q}: +{Me.Money - moneyBeforeSell}g (now {Me.Money}g)."
                : $"Clicked {q} but the shop didn't buy it - click one of the listed items to buy instead.";
        }

        // Buying: like a player, each purchase lands on the cursor and has to be clicked into the
        // backpack. We do that per unit: click the item, click a bag slot.
        int idx = FindShopItem(shop, q);
        if (idx < 0)
            return $"the shop's current page has no '{q}' - click=\"next\" to scroll, or pick from the list";
        var salable = shop.forSale[idx];
        string qid = (salable as Item)?.QualifiedItemId ?? "";
        int qtyBefore = qid.Length > 0 ? Me.Items.CountId(qid) : 0;
        int moneyBeforeBuy = Me.Money;
        int clicks = Math.Clamp(n, 1, 25);
        int done = 0;
        for (int i = 0; i < clicks; i++)
        {
            if (Game1.activeClickableMenu is not ShopMenu cur || cur != shop)
                break;
            int curIdx = FindShopItem(cur, q);
            if (curIdx < 0)
                break; // sold out or scrolled away
            var btn = cur.forSaleButtons[curIdx - cur.currentItemIndex];
            cur.receiveLeftClick(btn.bounds.Center.X, btn.bounds.Center.Y); // buys; item sits on the cursor
            DepositHeld(cur);                                               // click it into the backpack
            done++;
        }
        int gained = qid.Length > 0 ? Me.Items.CountId(qid) - qtyBefore : 0;
        int spent = moneyBeforeBuy - Me.Money;
        if (gained > 0)
            return $"Bought {gained} {salable.DisplayName} for {spent}g (money: {Me.Money}g).";
        if (shop.heldItem != null && shop.heldItem.DisplayName.Equals(salable.DisplayName, StringComparison.OrdinalIgnoreCase))
            return $"Bought 1 {salable.DisplayName} ({spent}g) but your backpack is full - it's still in your hand; close=... or make room and click again.";
        if (done > 0 && spent == 0)
            return $"the clicks on '{q}' didn't go through - you may be out of money (you have {Me.Money}g)";
        return $"nothing was bought - '{q}' may be sold out or the shop needs a scroll";
    }

    /// <summary>Click a backpack slot to put the cursor item away (like the player putting it in their bag).</summary>
    private static void DepositHeld(ShopMenu shop)
    {
        if (shop.heldItem is not Item held || shop.inventory?.inventory == null)
            return;
        // First a slot the stack can join, then an empty slot, then anything (it swaps).
        ClickableComponent? slot = shop.inventory.inventory.FirstOrDefault(s =>
                s.item != null && s.item.canStackWith(held) && s.item.Stack + held.Stack <= held.maximumStackSize())
            ?? shop.inventory.inventory.FirstOrDefault(s => s.item == null)
            ?? shop.inventory.inventory.FirstOrDefault(s => s.item != null);
        if (slot == null)
            return;
        shop.receiveLeftClick(slot.bounds.Center.X, slot.bounds.Center.Y);
    }

    private static int FindShopItem(ShopMenu shop, string name)
    {
        for (int k = 0; k < shop.forSaleButtons.Count; k++)
        {
            int idx = shop.currentItemIndex + k;
            if (idx >= shop.forSale.Count || shop.forSale[idx] == null)
                continue;
            if (Matches((Item)shop.forSale[idx]!, name))
                return idx;
        }
        return -1;
    }

    // ---------------------------------------------------------------- chests / shipping bin

    private static string Grab(ItemGrabMenu grab)
    {
        string what = grab.shippingBin ? "the shipping bin"
            : grab.sourceItem is Chest { fridge.Value: true } ? "the fridge"
            : grab.sourceItem is Chest ? "a chest"
            : "a grab-menu";
        var sb = new StringBuilder();
        sb.Append($"[{what}]");
        if (grab.heldItem != null)
            sb.Append($" (you're holding {grab.heldItem.DisplayName} x{grab.heldItem.Stack})");
        var inside = (grab.ItemsToGrabMenu?.actualInventory ?? new List<Item>()).Where(i => i != null).ToList();
        if (grab.shippingBin)
        {
            // The bin menu shows what's inside; items are added by placing them on the bin.
            if (inside.Count > 0)
                sb.Append("\nInside: " + string.Join(", ", inside.Select(i => i!.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName)) + ".");
            sb.Append("\nTo add items: close this, then equip an item and click its spot on the bin (place x,y on the bin).");
            sb.Append("\nActions: click=\"close\" when done.");
            return sb.ToString();
        }
        if (inside.Count > 0)
            sb.Append("\nInside: " + string.Join(", ", inside.Select(i => i!.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName)) + ".");
        var mine = Me.Items.Where(i => i != null).ToList();
        sb.Append("\nYour side: " + (mine.Count == 0 ? "(empty)" : string.Join(", ", mine.Select(i => i!.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName))));
        sb.Append("\nActions: click=\"take {item}\" (from inside), click=\"store {item}\" (one of yours), click=\"close\".");
        return sb.ToString();
    }

    private static string GrabClick(ItemGrabMenu grab, string element)
    {
        string q = element.Trim();
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
            return Close();
        if (grab.shippingBin)
        {
            // The bin menu is just an overview; shipping happens by placing items on the bin.
            return "the bin menu is only a view - close it, then equip the item and click its spot on the bin "
                + "(place x,y on the bin) to ship it.";
        }
        bool take = q.StartsWith("take ", StringComparison.OrdinalIgnoreCase);
        bool store = q.StartsWith("store ", StringComparison.OrdinalIgnoreCase) || q.StartsWith("put ", StringComparison.OrdinalIgnoreCase);
        if (!take && !store)
            return "say click=\"take <item>\" or click=\"store <item>\"";
        string name = q.Length > 5 ? q[5..].Trim() : q; // after "take "/"store "

        if (take)
        {
            var list = grab.ItemsToGrabMenu.actualInventory;
            var grid = grab.ItemsToGrabMenu.inventory;
            for (int i = 0; i < list.Count && i < grid.Count; i++)
            {
                if (list[i] != null && Matches(list[i]!, name))
                {
                    grab.receiveLeftClick(grid[i].bounds.Center.X, grid[i].bounds.Center.Y);
                    return $"Took {name} from the {(grab.shippingBin ? "shipping bin" : "menu")}.";
                }
            }
            return $"there's no '{name}' inside";
        }

        // store: find the item in our own bag, click its slot (picks it up), then click into the
        // collection area to drop it - the same two clicks a player makes.
        for (int i = 0; i < Me.Items.Count && i < grab.inventory.inventory.Count; i++)
        {
            if (Me.Items[i] != null && Matches(Me.Items[i]!, name))
            {
                grab.receiveLeftClick(grab.inventory.inventory[i].bounds.Center.X, grab.inventory.inventory[i].bounds.Center.Y);
                if (grab.heldItem == null)
                    return $"couldn't pick up {name}";
                var area = grab.ItemsToGrabMenu;
                grab.receiveLeftClick(area.xPositionOnScreen + 20, area.yPositionOnScreen + Math.Max(10, area.height / 2));
                return $"Stored {name} in the {(grab.shippingBin ? "shipping bin" : "menu")}.";
            }
        }
        return $"you don't carry '{name}'";
    }

    // ---------------------------------------------------------------- level ups

    private static string Level(LevelUpMenu level)
    {
        string skillName = Reflection.GetField<int>(level, "currentSkill").GetValue() switch
        {
            0 => "Farming", 1 => "Fishing", 2 => "Foraging", 3 => "Mining", 4 => "Combat", _ => "a skill",
        };
        int currentLevel = Reflection.GetField<int>(level, "currentLevel").GetValue();
        var sb = new StringBuilder();
        sb.Append($"Level up! {skillName} reached level {currentLevel}.");
        if (level.isProfessionChooser)
        {
            var left = Reflection.GetField<List<string>>(level, "leftProfessionDescription").GetValue();
            var right = Reflection.GetField<List<string>>(level, "rightProfessionDescription").GetValue();
            sb.Append("\nChoose a profession (click=\"left\" or click=\"right\"):");
            sb.Append($"\n- left: {string.Join(" ", left)}");
            sb.Append($"\n- right: {string.Join(" ", right)}");
        }
        else
        {
            sb.Append("\nclick=\"ok\" to continue.");
        }
        return sb.ToString();
    }

    private static string LevelClick(LevelUpMenu level, string element)
    {
        string q = element.Trim().ToLowerInvariant();
        if (q is "ok" or "continue" or "close")
        {
            level.receiveLeftClick(level.okButton.bounds.Center.X, level.okButton.bounds.Center.Y);
            return "OK.";
        }
        var target = q is "left" or "right" ? (q == "left" ? level.leftProfession : level.rightProfession)
            : level.isProfessionChooser && q.Contains("left") ? level.leftProfession
            : level.isProfessionChooser && q.Contains("right") ? level.rightProfession : null;
        if (target == null)
            return "click=\"left\" or click=\"right\" to pick a profession";
        level.receiveLeftClick(target.bounds.Center.X, target.bounds.Center.Y);
        return "Chose a profession.";
    }
}