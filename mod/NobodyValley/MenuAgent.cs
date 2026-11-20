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
            JunimoNoteMenu note => Bundle(note),
            GameMenu game => Game(game),
            _ => null, // dialogues/letters/ready checks are screen messages
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
            JunimoNoteMenu note => BundleClick(note, element, n),
            GameMenu game => GameClick(game, element, n),
            _ => $"there's no clickable menu open right now (clickable: shop/chest/bin/bundle/crafting/level-up)",
        };
    }

    /// <summary>Close the current menu, as if the player pressed the close key. When something is on
    /// the cursor, that means the vanilla ESC flow (which collects the item into the backpack).</summary>
    public static string Close()
    {
        var m = Game1.activeClickableMenu;
        if (m == null)
            return "there's no menu open";
        if (m is ShopMenu shop && shop.heldItem != null)
        {
            string held = shop.heldItem.DisplayName;
            shop.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Escape); // the vanilla ESC: put the cursor item back
            if (Game1.activeClickableMenu is ShopMenu still)
                still.exitThisMenu(playSound: true);                     // and leave, like a second ESC
            return $"Closed the shop; {held} went back into your backpack.";
        }
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
            sb.Append($"(you're holding {shop.heldItem.DisplayName} x{shop.heldItem.Stack} on the cursor - it isn't in your backpack yet; close when done)");
        sb.Append($" Shop is open; you have {Me.Money}g. The whole list is here - click any item to buy 1 (n=N for N). Click one of YOUR items (the ones you carry) to sell it.");
        if (shop.tabButtons is { Count: > 1 })
            sb.Append("\nTabs: " + string.Join(", ", shop.tabButtons.Select(t => $"\"{t.label}\"")) + " (click=\"tab {name}\").");
        int shown = 0;
        for (int i = 0; i < shop.forSale.Count && shown < 60; i++)
        {
            var salable = shop.forSale[i];
            if (salable == null)
                continue;
            int price = shop.itemPriceAndStock.TryGetValue(salable, out var info) ? info.Price : salable.salePrice();
            int stock = shop.itemPriceAndStock.TryGetValue(salable, out var info2) ? info2.Stock : -1;
            string stockText = stock <= 0 ? "" : stock >= int.MaxValue / 2 ? "" : $", {stock} left";
            sb.Append($"\n- {salable.DisplayName} ({price}g{stockText})");
            shown++;
        }
        if (shop.forSale.Count > shown)
            sb.Append($"\n... and {shop.forSale.Count - shown} more - switch tabs to see them; any click on a listed item works.");
        sb.Append("\nActions: click=\"<item name>\" (buy/sell 1, n=N for N), click=\"tab <name>\", click=\"close\".");
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
            shop.safetyTimer = 0;
            shop.receiveLeftClick(shop.downArrow.bounds.Center.X, shop.downArrow.bounds.Center.Y);
            return shop.currentItemIndex > before ? "Scrolled down." : "Already at the bottom.";
        }
        if (q.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            int before = shop.currentItemIndex;
            shop.safetyTimer = 0;
            shop.receiveLeftClick(shop.upArrow.bounds.Center.X, shop.upArrow.bounds.Center.Y);
            return shop.currentItemIndex < before ? "Scrolled up." : "Already at the top.";
        }
        // Selling: click the item in the shop's bottom pane (your items, index-aligned with the
        // real bag), like a player. The pane only reacts to what this shop buys.
        int carried = -1;
        for (int i = 0; i < Me.Items.Count && i < shop.inventory.inventory.Count; i++)
        {
            if (Me.Items[i] != null && Matches(Me.Items[i]!, q))
            {
                carried = i;
                break;
            }
        }
        if (carried >= 0)
        {
            int moneyBeforeSell = Me.Money;
            int sold = 0;
            int clicksSell = Math.Clamp(n, 1, 25);
            for (int c = 0; c < clicksSell; c++)
            {
                int sellIdx = -1;
                for (int j = 0; j < Me.Items.Count && j < shop.inventory.inventory.Count; j++)
                    if (Me.Items[j] != null && Matches(Me.Items[j]!, q))
                    {
                        sellIdx = j;
                        break;
                    }
                if (sellIdx < 0)
                    break; // sold the whole stack
                shop.safetyTimer = 0;
                shop.receiveLeftClick(shop.inventory.inventory[sellIdx].bounds.Center.X, shop.inventory.inventory[sellIdx].bounds.Center.Y);
                sold++;
            }
            int gained = Me.Money - moneyBeforeSell;
            return gained > 0
                ? $"Sold {q}: +{gained}g (now {Me.Money}g)."
                : $"Clicked {q} in the pane but nothing sold - this shop may not buy {q} (ship it instead).";
        }

        // A tab click: switch the shop's category, like a player clicking its tab.
        if (q.StartsWith("tab ", StringComparison.OrdinalIgnoreCase) && shop.tabButtons is { Count: > 0 })
        {
            string tabName = q["tab ".Length..];
            for (int i = 0; i < shop.tabButtons.Count; i++)
            {
                var tab = shop.tabButtons[i];
                if (tab.label.Equals(tabName, StringComparison.OrdinalIgnoreCase) || tab.name.Equals(tabName, StringComparison.OrdinalIgnoreCase))
                {
                    shop.receiveLeftClick(tab.bounds.Center.X, tab.bounds.Center.Y);
                    return $"Switched to the {tabName} tab.";
                }
            }
            return $"no tab '{tabName}' - tabs are: " + string.Join(", ", shop.tabButtons.Select(t => t.label));
        }

        // Buying: the whole stock is listed, so scroll to the item first if it isn't on the
        // current page, then click its button once per unit wanted (purchases stack on the cursor).
        int idx = FindShopItem(shop, q);
        if (idx < 0)
            return $"the shop doesn't sell '{q}' - pick from the list (or switch tabs)";
        ScrollTo(shop, idx);
        var salable = shop.forSale[idx];
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
            ScrollTo(cur, curIdx);
            var btn = cur.forSaleButtons[curIdx - cur.currentItemIndex];
            cur.safetyTimer = 0; // purchases arm a repeat-click guard; clears like the real timer would
            cur.receiveLeftClick(btn.bounds.Center.X, btn.bounds.Center.Y); // buys; stacks on the cursor
            done++;
        }
        // The vanilla purchase sits on the cursor; closing the shop returns it to your backpack
        // (a full backpack shows an "items that didn't fit" menu the agent can clear out).
        int spent = moneyBeforeBuy - Me.Money;
        if (shop.heldItem is Item heldH && heldH.DisplayName.Equals(salable.DisplayName, StringComparison.OrdinalIgnoreCase))
            return $"Bought {done} {salable.DisplayName} for {spent}g (money: {Me.Money}g) - it's on your cursor; click=\"close\" to put it in your backpack.";
        if (done > 0 && spent == 0)
            return $"the clicks on '{q}' didn't go through - you may be out of money (you have {Me.Money}g)";
        return $"nothing was bought - '{q}' may be sold out or the shop needs a scroll";
    }



    private static int FindShopItem(ShopMenu shop, string name)
    {
        for (int i = 0; i < shop.forSale.Count; i++)
        {
            if (shop.forSale[i] != null && Matches((Item)shop.forSale[i]!, name))
                return i;
        }
        return -1;
    }

    /// <summary>Bring an item's page into view so its button is clickable (the shop only clicks the
    /// visible rows). Direct, like scrolling - the agent sees the whole list anyway.</summary>
    private static void ScrollTo(ShopMenu shop, int idx)
    {
        int pageSize = Math.Max(1, shop.forSaleButtons.Count);
        if (idx < shop.currentItemIndex || idx >= shop.currentItemIndex + pageSize)
            shop.currentItemIndex = Math.Clamp(idx, 0, Math.Max(0, shop.forSale.Count - pageSize));
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

    // ---------------------------------------------------------------- junimo bundles

    private static string Bundle(JunimoNoteMenu menu)
    {
        var sb = new StringBuilder("[Junimo bundles]");
        if (menu.specificBundlePage && menu.currentPageBundle is { } b)
        {
            sb.Append($"\nBundle: {b.name}");
            if (menu.purchaseButton != null)
            {
                // The Vault: paid in gold.
                int cost = b.ingredients.LastOrDefault().stack;
                sb.Append($"\nNeeds {cost}g ({Me.Money}g).");
                sb.Append("\nActions: click=\"pay\", click=\"back\", click=\"close\".");
                return sb.ToString();
            }
            foreach (var need in b.ingredients)
                if (!need.completed)
                    sb.Append($"\n- needs {IngredientName(need)}");
            sb.Append("\nActions: click=\"give {item}\" (donate one you carry), click=\"back\", click=\"close\".");
            return sb.ToString();
        }
        var open = menu.bundles.Where(x => !x.complete && x.canBeClicked()).Select(x => x.name).ToList();
        sb.Append(open.Count == 0 ? " Everything here is done." : " Open bundles:");
        foreach (var name in open)
            sb.Append($"\n- {name}  (click=\"bundle {name}\")");
        if (menu.heldItem != null)
            sb.Append($"\n(you're holding {menu.heldItem.DisplayName} x{menu.heldItem.Stack} on your cursor - click=\"bag\" to put it away)");
        sb.Append("\nActions: click=\"bundle {name}\", click=\"bag\" (put a cursor item away), click=\"close\".");
        return sb.ToString();
    }

    private static string IngredientName(BundleIngredientDescription need)
    {
        if (need.category is int cat)
        {
            string catName = cat switch
            {
                -2 => "Mineral", -75 => "Foraged item", -4 => "Fish", -5 => "Egg", -6 => "Milk", _ => $"category {cat}",
            };
            return $"{need.stack} {catName}" + (need.quality >= 2 ? " (gold)" : "");
        }
        return $"{need.stack} {ItemRegistry.GetDataOrErrorItem(need.id).DisplayName}" + (need.quality >= 2 ? " (gold)" : "");
    }

    private static string BundleClick(JunimoNoteMenu menu, string element, int n)
    {
        string q = element.Trim();
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
        {
            if (menu.specificBundlePage && menu.upperRightCloseButton != null)
            {
                menu.receiveLeftClick(menu.upperRightCloseButton.bounds.Center.X, menu.upperRightCloseButton.bounds.Center.Y);
                return "Closed the bundle page.";
            }
            menu.exitThisMenu(playSound: true);
            return "Closed the bundle menu.";
        }
        if (q.StartsWith("bundle ", StringComparison.OrdinalIgnoreCase))
        {
            string name = q["bundle ".Length..];
            var bundle = menu.bundles.FirstOrDefault(x => !x.complete && x.name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (bundle == null)
                return $"no open bundle named '{name}' - pick from the list";
            menu.receiveLeftClick(bundle.bounds.Center.X, bundle.bounds.Center.Y);
            return $"Opened the {name} bundle.";
        }

        // On a bundle's detail page.
        if (!menu.specificBundlePage || menu.currentPageBundle is not { } b)
            return "open a bundle first (click=\"bundle {name}\")";
        if (q.Equals("back", StringComparison.OrdinalIgnoreCase) && menu.backButton != null)
        {
            menu.receiveLeftClick(menu.backButton.bounds.Center.X, menu.backButton.bounds.Center.Y);
            return "Back to the bundle list.";
        }
        if (q.Equals("pay", StringComparison.OrdinalIgnoreCase))
        {
            if (menu.purchaseButton == null)
                return "this bundle isn't paid in gold";
            menu.receiveLeftClick(menu.purchaseButton.bounds.Center.X, menu.purchaseButton.bounds.Center.Y);
            return $"Paid for the {b.name} bundle.";
        }
        if (q.StartsWith("give ", StringComparison.OrdinalIgnoreCase))
        {
            string name = q["give ".Length..];
            // Pick the item up from our grid, then click the empty ingredient slot that accepts it.
            for (int i = 0; i < Me.Items.Count && i < menu.inventory.inventory.Count; i++)
            {
                if (Me.Items[i] == null || !Matches(Me.Items[i]!, name))
                    continue;
                menu.receiveLeftClick(menu.inventory.inventory[i].bounds.Center.X, menu.inventory.inventory[i].bounds.Center.Y);
                if (menu.heldItem == null)
                    return $"couldn't pick up {name} - the bundle only lets you take items it needs (they're the only clickable ones)";
                for (int l = 0; l < menu.ingredientSlots.Count; l++)
                {
                    if (menu.ingredientSlots[l].item == null && b.canAcceptThisItem(menu.heldItem, menu.ingredientSlots[l]))
                    {
                        menu.receiveLeftClick(menu.ingredientSlots[l].bounds.Center.X, menu.ingredientSlots[l].bounds.Center.Y);
                        return $"Donated {name} to the {b.name} bundle.";
                    }
                }
                // No empty slot accepts it (wrong quality or not the full count) - put it back
                // into the bag. NEVER click outside: the menu would throw the item on the ground.
                if (ReturnHeld(menu))
                    return $"{name} doesn't fit the {b.name} bundle right now (wrong quality or too few?) - put back in your bag";
                return $"{name} doesn't fit, and there's no free bag slot - it's still on your cursor";
            }
            return $"you don't carry '{name}'";
        }
        if (q.Equals("bag", StringComparison.OrdinalIgnoreCase))
            return ReturnHeld(menu)
                ? "Put it back in your bag."
                : "No free bag slot - make room (store something) and click bag again.";
        return "say click=\"give <item>\", click=\"bag\", click=\"back\", or click=\"pay\"";
    }

    /// <summary>Click the cursor item back into a free/stacking bag slot. Searches the REAL bag by
    /// index (component.item is unreliable in some menus), and never swaps slots.</summary>
    private static bool ReturnHeld(JunimoNoteMenu menu)
    {
        if (menu.heldItem is not Item held || menu.inventory?.inventory == null)
            return false;
        int idx = FindBagSlot(held);
        if (idx < 0 || idx >= menu.inventory.inventory.Count)
            return false;
        menu.receiveLeftClick(menu.inventory.inventory[idx].bounds.Center.X, menu.inventory.inventory[idx].bounds.Center.Y);
        return menu.heldItem == null;
    }

    /// <summary>An index in the real bag where <paramref name="held"/> can go (stack or empty), or -1.</summary>
    private static int FindBagSlot(Item held)
    {
        for (int i = 0; i < Me.Items.Count; i++)
            if (Me.Items[i] != null && Me.Items[i]!.canStackWith(held) && Me.Items[i]!.Stack + held.Stack <= held.maximumStackSize())
                return i;
        for (int i = 0; i < Me.Items.Count; i++)
            if (Me.Items[i] == null)
                return i;
        return -1;
    }

    // ---------------------------------------------------------------- crafting (the GameMenu)

    private static string Game(GameMenu game)
    {
        if (game.pages.ElementAtOrDefault(game.currentTab) is not CraftingPage page)
            return "[backpack menu] that tab isn't wired up for clicking yet - close it (click=\"close\").";
        var sb = new StringBuilder("Crafting menu:");
        var recipes = page.pagesOfCraftingRecipes.ElementAtOrDefault(page.currentCraftingPage);
        if (recipes is { Count: > 0 })
        {
            foreach (var (_, recipe) in recipes)
            {
                string parts = string.Join(", ", recipe.recipeList.Select(kv => IngredientForCraft(kv)));
                sb.Append($"\n- {recipe.DisplayName}: {parts}"
                    + (recipe.doesFarmerHaveIngredientsInInventory() ? " (you have these)" : " (missing ingredients)"));
            }
        }
        if (page.heldItem != null)
            sb.Append($"\n(you're holding {page.heldItem.DisplayName} x{page.heldItem.Stack} on your cursor - click=\"bag\" to put it in a free slot)");
        if (page.pagesOfCraftingRecipes.Count > 1)
            sb.Append($"\n(page {page.currentCraftingPage + 1} of {page.pagesOfCraftingRecipes.Count} - click=\"next\"/\"prev\")");
        sb.Append("\nActions: click=\"craft {name}\" (n=N for N; each craft is also clicked into a bag slot for you), click=\"bag\", click=\"next\"/\"prev\", click=\"close\".");
        return sb.ToString();
    }

    private static string IngredientForCraft(KeyValuePair<string, int> kv)
    {
        string what = int.TryParse(kv.Key, out int cat) && cat < 0
            ? $"any {cat switch { -5 => "egg", -6 => "milk", -75 => "vegetable", -80 => "flower", _ => $"category {cat}" }}"
            : ItemRegistry.GetDataOrErrorItem(kv.Key).DisplayName;
        return $"{kv.Value} {what}";
    }

    private static string GameClick(GameMenu game, string element, int n)
    {
        string q = element.Trim();
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
        {
            game.exitThisMenu(playSound: true);
            return "Closed the menu.";
        }
        if (game.pages.ElementAtOrDefault(game.currentTab) is not CraftingPage page)
            return "that tab has nothing clickable yet";
        if (q.Equals("bag", StringComparison.OrdinalIgnoreCase))
        {
            if (page.heldItem == null)
                return "your cursor is empty already";
            return DepositHeld(page) ? "Put it in your bag." : "No free slot - make room (store things) and click bag again.";
        }
        if (q.Equals("next", StringComparison.OrdinalIgnoreCase) && page.downButton != null)
        {
            page.receiveLeftClick(page.downButton.bounds.Center.X, page.downButton.bounds.Center.Y);
            return "Next crafting page.";
        }
        if (q.Equals("prev", StringComparison.OrdinalIgnoreCase) && page.upButton != null)
        {
            page.receiveLeftClick(page.upButton.bounds.Center.X, page.upButton.bounds.Center.Y);
            return "Previous crafting page.";
        }
        string name = q.StartsWith("craft ", StringComparison.OrdinalIgnoreCase) ? q["craft ".Length..] : q;
        var recipes = page.pagesOfCraftingRecipes[page.currentCraftingPage];
        foreach (var (comp, recipe) in recipes)
        {
            if (!recipe.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase))
                continue;
            int made = 0;
            int want = Math.Clamp(n, 1, 25);
            for (int i = 0; i < want; i++)
            {
                if (Game1.activeClickableMenu is not GameMenu || game.pages.ElementAtOrDefault(game.currentTab) is not CraftingPage cur)
                    break;
                if (cur.heldItem != null && !DepositHeld(cur))
                    break; // cursor is stuck full - deal with it first
                cur.receiveLeftClick(comp.bounds.Center.X, comp.bounds.Center.Y); // vanilla: item appears on the cursor
                if (cur.heldItem == null)
                    break; // no ingredients
                made++;
                if (!DepositHeld(cur))
                    break; // crafted but the cursor is stuck full; the menu note mentions it
            }
            return made > 0
                ? $"Crafted {made} {recipe.DisplayName}{((Game1.activeClickableMenu is GameMenu gm && gm.pages.ElementAtOrDefault(gm.currentTab) is CraftingPage cp && cp.heldItem != null) ? " - one is on your cursor; click=bag when you have room" : "")}."
                : $"Clicked {recipe.DisplayName} - you're missing ingredients; read the recipe list.";
        }
        return $"no recipe '{name}' on this page - click=\"next\" to scroll";
    }

    /// <summary>Click a bag slot to put the cursor item away (the second half of a craft click).
    /// Index-aligned with the real bag; only stackable/empty slots, never swaps.</summary>
    private static bool DepositHeld(CraftingPage page)
    {
        if (page.heldItem is not Item held || page.inventory?.inventory == null)
            return false;
        int idx = FindBagSlot(held);
        if (idx < 0 || idx >= page.inventory.inventory.Count)
            return false;
        page.receiveLeftClick(page.inventory.inventory[idx].bounds.Center.X, page.inventory.inventory[idx].bounds.Center.Y);
        return page.heldItem == null;
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
        // LevelUpMenu.receiveLeftClick is empty (it polls the mouse), so do what the buttons do.
        if (!level.readyToClose() || !level.isActive)
            return "the level-up screen isn't ready yet";
        if (!level.isProfessionChooser || q is "ok" or "continue" or "close" or "skip")
        {
            level.okButtonClicked();
            return "OK.";
        }
        var choices = Reflection.GetField<List<int>>(level, "professionsToChoose").GetValue();
        int pick = q.Contains("right") && choices.Count > 1 ? 1 : choices.Count > 0 ? 0 : -1;
        if (pick < 0)
            return "click=\"left\" or click=\"right\" to pick a profession";
        Game1.player.professions.Add(choices[pick]);
        level.getImmediateProfessionPerk(choices[pick]);
        level.isActive = false;
        level.informationUp = false;
        level.RemoveLevelFromLevelList();
        return "Chose a profession.";
    }
}