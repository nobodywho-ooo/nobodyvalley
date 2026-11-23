using System.Collections;
using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using NobodyValley.Jobs;
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

    // Human-like input pacing: no menu interaction before this game tick, and a random gap after
    // each one (the "move the mouse, click" feel instead of everything landing in one frame).
    private static int nextInteractionTick;

    /// <summary>Wait until this interaction is due. The next gap is armed when an interaction
    /// finishes (ArmPace), so consecutive agent requests get a natural human-like spacing.</summary>
    public static IEnumerator PacedWait()
    {
        while (Game1.ticks < nextInteractionTick)
            yield return null;
    }

    private static void ArmPace() => nextInteractionTick = Game1.ticks + Game1.random.Next(10, 30); // ~170-500 ms
    internal static void ArmPacingNow() => ArmPace();

    // A menu that just appeared (level-up screens, freshly-opened shops) spends a few frames
    // animating its intro and actually becoming clickable (LevelUpMenu.readyToClose, button hit
    // boxes). If we click instantly - before the previous action's own pace even - the click lands
    // on nothing and the agent sees "not ready yet" and has to re-plan. Give it a moment.
    public static IEnumerator SettleWait()
    {
        int at = Game1.ticks;
        int until = at + 40; // ~0.65s at 60fps: plenty for a menu's intro
        while (Game1.ticks < until)
            yield return null;
    }

    /// <summary>Describe the current menu as the agent should see it, or null when none is open.</summary>
    public static string? Describe()
    {
        var m = Game1.activeClickableMenu;
        return m switch
        {
            ShopMenu shop => Shop(shop),
            PurchaseAnimalsMenu animals => Animals(animals),
            CarpenterMenu carpentry => Carpentry(carpentry),
            ItemGrabMenu grab => Grab(grab),
            LevelUpMenu level => Level(level),
            JunimoNoteMenu note => Bundle(note),
            GameMenu game => Game(game),
            CraftingPage page => Page(page), // standalone: the kitchen stove cooks, a workbench crafts
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
            PurchaseAnimalsMenu animals => AnimalClick(animals, element),
            CarpenterMenu carpentry => CarpentryClick(carpentry, element),
            ItemGrabMenu grab => GrabClick(grab, element),
            LevelUpMenu level => LevelClick(level, element),
            JunimoNoteMenu note => BundleClick(note, element, n),
            GameMenu game => GameClick(game, element, n),
            CraftingPage page => PageClick(page, element, n),
            _ => $"there's no clickable menu open right now (clickable: shop/animals/construction/chest/bin/bundle/crafting/cooking/level-up)",
        };
    }

    /// <summary>A paced menu interaction, run as a job: a natural pause first, and multi-clicks
    /// (buying n, crafting n) spaced like a real player's clicks.</summary>
    public static IEnumerator PacedClick(Job job, string element, int n)
    {
        yield return PacedWait();
        var m = Game1.activeClickableMenu;
        if (m is ShopMenu shop && IsShopBuy(shop, element))
            yield return PacedShopBuy(job, shop, element, n);
        else if (m is GameMenu game && game.pages.ElementAtOrDefault(game.currentTab) is CraftingPage page
                 && element.Trim().StartsWith("craft ", StringComparison.OrdinalIgnoreCase))
            yield return PacedCraft(job, page, element, n);
        else if (m is PurchaseAnimalsMenu)
            yield return PacedAnimalClick(job, element);
        else if (m is CarpenterMenu)
            yield return PacedCarpentryClick(job, element);
        else if (m is CraftingPage canvas && (element.Trim().StartsWith("cook ", StringComparison.OrdinalIgnoreCase)
                                              || element.Trim().StartsWith("craft ", StringComparison.OrdinalIgnoreCase)))
            yield return PacedCraft(job, canvas, element, n); // the kitchen stove (cook) or a workbench (craft)
        else
        {
            // Give a menu that just appeared (e.g. a level-up screen that animates its intro
            // before the buttons are live) a moment to become clickable, so the click lands.
            yield return SettleWait();
            if (m is LevelUpMenu levelUp)
            {
                int guard = 0;
                while ((!levelUp.readyToClose() || !levelUp.isActive) && guard++ < 120) // ~2s
                    yield return null;
            }
            job.Summary = Click(element, n); // chests, bundles, single-shop clicks, ...
            ArmPace();
        }
    }

    /// <summary>A paced click in Marnie's animal shop / Robin's shop. Buying (and backing out)
    /// fades to black to show the farm; wait the fade out first, like a player waiting for the
    /// screen to change.</summary>
    public static IEnumerator PacedAnimalClick(Job job, string element)
    {
        yield return FadeWaitAndClick(job, element);
    }

    /// <summary>A paced click in Robin's construction menu (builds/demolish/move fade to the farm).</summary>
    public static IEnumerator PacedCarpentryClick(Job job, string element)
    {
        yield return FadeWaitAndClick(job, element);
    }

    private static IEnumerator FadeWaitAndClick(Job job, string element)
    {
        int guard = 0;
        while (Game1.IsFading() && guard++ < 240) // ~4s at 60fps: the placement fades
            yield return null;
        yield return PacedWait();
        job.Summary = Click(element, 1);
        ArmPace();
    }

    public static IEnumerator PacedClose(Job job)
    {
        yield return PacedWait();
        job.Summary = Close();
        ArmPace();
    }

    /// <summary>Whether a shop click is a buy (mirrors ShopClick's decision for bare names).</summary>
    private static bool IsShopBuy(ShopMenu shop, string element)
    {
        string q = element.Trim();
        if (q.StartsWith("sell ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (q.StartsWith("buy ", StringComparison.OrdinalIgnoreCase))
            return true;
        // Navigation/shop-exit commands are handled by ShopClick on the slow path; don't send
        // them down the buy route (where "close" would be treated as an item to buy and miss).
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase)
            || q.Equals("leave", StringComparison.OrdinalIgnoreCase)
            || q.Equals("continue", StringComparison.OrdinalIgnoreCase)
            || q.Equals("dismiss", StringComparison.OrdinalIgnoreCase)
            || q.Equals("exit", StringComparison.OrdinalIgnoreCase)
            || q.Equals("back", StringComparison.OrdinalIgnoreCase)
            || q.Equals("done", StringComparison.OrdinalIgnoreCase)
            || q.Equals("ok", StringComparison.OrdinalIgnoreCase)
            || q.Equals("next", StringComparison.OrdinalIgnoreCase)
            || q.Equals("prev", StringComparison.OrdinalIgnoreCase)
            || q.StartsWith("tab ", StringComparison.OrdinalIgnoreCase))
            return false;
        return FindShopItemExact(shop, q) >= 0 || FindCarriedExact(q) < 0;
    }

    private static IEnumerator PacedShopBuy(Job job, ShopMenu shop, string element, int n)
    {
        string q = element.Trim();
        if (q.StartsWith("buy ", StringComparison.OrdinalIgnoreCase))
            q = q["buy ".Length..].Trim();
        int idx = FindShopItem(shop, q);
        if (idx < 0)
        {
            job.Summary = $"the shop doesn't sell '{q}' - pick from the list (or switch tabs)";
            yield break;
        }
        var salable = shop.forSale[idx];
        int moneyBeforeBuy = Me.Money;
        int want = Math.Clamp(n, 1, 25);
        int done = 0;
        while (done < want && Game1.activeClickableMenu is ShopMenu cur && cur == shop)
        {
            // Scroll toward the item first, one visible row per step - like a player clicking
            // the shop's arrow (the list visibly moves; no warping to the item).
            int targetIdx = FindShopItem(cur, q);
            int steps = 0;
            while (targetIdx >= 0 && !IsVisible(cur, targetIdx) && ScrollStep(cur, targetIdx) && steps++ < 60)
            {
                int w = Game1.random.Next(6, 14); // ~100-230 ms per scroll click
                for (int t = 0; t < w; t++)
                    yield return null;
            }
            if (!TryBuyOne(cur, salable.DisplayName))
                break;
            done++;
            job.Summary = $"Bought {done} {salable.DisplayName} (money: {Me.Money}g).";
            if (done < want)
            {
                int wait = Game1.random.Next(8, 20); // ~130-330 ms between clicks
                for (int t = 0; t < wait; t++)
                    yield return null;
            }
        }
        int spent = moneyBeforeBuy - Me.Money;
        if (shop.heldItem is Item heldH && heldH.DisplayName.Equals(salable.DisplayName, StringComparison.OrdinalIgnoreCase))
            job.Summary = $"Bought {done} {salable.DisplayName} for {spent}g (money: {Me.Money}g) - it's on your cursor; click=\"close\" to put it in your backpack.";
        else if (done == 0)
            job.Summary = $"the clicks on '{q}' didn't buy anything - check your money ({Me.Money}g) and the stock";
        ArmPace();
    }

    private static IEnumerator PacedCraft(Job job, CraftingPage page, string element, int n)
    {
        string q = element.Trim();
        if (q.StartsWith("cook ", StringComparison.OrdinalIgnoreCase))
            q = q["cook ".Length..].Trim();
        else if (q.StartsWith("craft ", StringComparison.OrdinalIgnoreCase))
            q = q["craft ".Length..].Trim();
        bool cook = page.cooking;
        var recipes = page.pagesOfCraftingRecipes[page.currentCraftingPage];
        foreach (var (comp, recipe) in recipes)
        {
            if (!recipe.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            int made = 0;
            int want = Math.Clamp(n, 1, 25);
            while (made < want && IsCurrentPage(page))
            {
                if (TryCraftOne(page, comp) == 0)
                    break;
                made++;
                job.Summary = $"{(cook ? "Cooked" : "Crafted")} {made} {recipe.DisplayName}.";
                if (made < want)
                {
                    int wait = Game1.random.Next(8, 20);
                    for (int t = 0; t < wait; t++)
                        yield return null;
                }
            }
            job.Summary = made > 0
                ? $"{(cook ? "Cooked" : "Crafted")} {made} {recipe.DisplayName}{(page.heldItem != null ? " - one is on your cursor; click=bag when you have room" : "")}."
                : $"Clicked {recipe.DisplayName} - you're missing ingredients{(cook ? " (the fridge counts)" : "")}; read the recipe list.";
            ArmPace();
            yield break;
        }
        if (job.Summary.Length == 0)
            job.Summary = $"no recipe '{q}' on this page - click=\"next\" to scroll";
        ArmPace();
    }

    /// <summary>Whether <paramref name="page"/> is the page the player is actually looking at
    /// (its tab of the GameMenu, or the standalone kitchen/workbench menu itself).</summary>
    private static bool IsCurrentPage(CraftingPage page) =>
        Game1.activeClickableMenu is GameMenu game
            ? game.pages.ElementAtOrDefault(game.currentTab) is CraftingPage cp && cp == page
            : Game1.activeClickableMenu == page;

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
        sb.Append("\nActions: click=\"<item name>\" (buy 1, n=N for N; if you also carry it, an exact name buys), click=\"sell <item>\" (sell what you carry), click=\"tab <name>\", click=\"close\".");
        return sb.ToString();
    }

    private static bool Matches(Item item, string name) =>
        item.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)
        || item.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static string ShopClick(ShopMenu shop, string element, int n)
    {
        string q = element.Trim();
        // The agent's generic "get past this" clicks (continue/ok/done/...), like 'close', mean
        // "get this shop out of my way" - never send them down the buy route.
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase)
            || q.Equals("leave", StringComparison.OrdinalIgnoreCase)
            || q.Equals("continue", StringComparison.OrdinalIgnoreCase)
            || q.Equals("dismiss", StringComparison.OrdinalIgnoreCase)
            || q.Equals("exit", StringComparison.OrdinalIgnoreCase)
            || q.Equals("back", StringComparison.OrdinalIgnoreCase)
            || q.Equals("done", StringComparison.OrdinalIgnoreCase)
            || q.Equals("ok", StringComparison.OrdinalIgnoreCase))
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
        // A tab click switches the shop's category, like a player clicking its tab.
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
        // "sell X" / "buy X" pick the pane; a bare name matches exact names first (the shop's
        // list wins ties, so "Potato Seeds" buys rather than selling the ones you carry), then
        // falls back to partial names.
        bool wantSell = q.StartsWith("sell ", StringComparison.OrdinalIgnoreCase);
        bool wantBuy = q.StartsWith("buy ", StringComparison.OrdinalIgnoreCase);
        if (wantSell || wantBuy)
            q = q[(wantSell ? "sell " : "buy ").Length..].Trim();
        int exactForSale = FindShopItemExact(shop, q);
        int exactCarried = FindCarriedExact(q);

        if (wantSell || (exactCarried >= 0 && exactForSale < 0))
        {
            // Selling: click the item in the shop's bottom pane (your items, index-aligned with
            // the real bag), like a player. The pane only reacts to what this shop buys.
            int sellIdx = exactCarried >= 0 ? exactCarried : FindCarried(q);
            if (sellIdx < 0)
                return wantSell ? $"you don't carry '{q}' - say sell and the name of something in your bag" : $"you don't carry '{q}'";
            int moneyBeforeSell = Me.Money;
            shop.safetyTimer = 0;
            shop.receiveLeftClick(shop.inventory.inventory[sellIdx].bounds.Center.X, shop.inventory.inventory[sellIdx].bounds.Center.Y);
            int gained = Me.Money - moneyBeforeSell;
            return gained > 0
                ? $"Sold {Me.Items[sellIdx]?.DisplayName ?? q}: +{gained}g (now {Me.Money}g)."
                : $"Clicked {q} in the pane but nothing sold - this shop may not buy it (ship it instead).";
        }

        // Buying: the whole stock is listed, so scroll to the item first if it isn't on the
        // current page, then click its button once per unit wanted (purchases stack on the cursor).
        int idx = FindShopItem(shop, q);
        if (idx < 0)
            return wantBuy
                ? $"the shop doesn't sell '{q}' - pick from the list (or switch tabs)"
                : FindCarried(q) >= 0
                    ? $"you carry '{q}' but this shop doesn't sell it and won't buy it - ship it instead"
                    : $"the shop doesn't sell '{q}' and you don't carry it - pick from the list";
        var salable = shop.forSale[idx];
        int moneyBeforeBuy = Me.Money;
        int clicks = Math.Clamp(n, 1, 25);
        int done = 0;
        for (int i = 0; i < clicks && TryBuyOne(shop, salable.DisplayName); i++)
            done++;
        // The vanilla purchase sits on the cursor; closing the shop returns it to your backpack
        // (a full backpack shows an "items that didn't fit" menu the agent can clear out).
        int spent = moneyBeforeBuy - Me.Money;
        if (shop.heldItem is Item heldH && heldH.DisplayName.Equals(salable.DisplayName, StringComparison.OrdinalIgnoreCase))
            return $"Bought {done} {salable.DisplayName} for {spent}g (money: {Me.Money}g) - it's on your cursor; click=\"close\" to put it in your backpack.";
        string? blocker = shop.heldItem is Item hb ? $"your cursor holds {hb.DisplayName} x{hb.Stack}" : done > 0 ? $"you may be out of money (you have {Me.Money}g)" : null;
        if (blocker != null)
            return $"the clicks on '{q}' didn't buy anything - {blocker}. (Put it away with click=\"close\", then buy again.)";
        return $"nothing was bought - '{q}' may be sold out or needs a scroll";
    }

    /// <summary>One buy click on the shop's button for <paramref name="desiredName"/>. Returns
    /// whether the click was made. Any different purchase on the cursor is put away first.</summary>
    private static bool TryBuyOne(ShopMenu shop, string desiredName)
    {
        if (Game1.activeClickableMenu is not ShopMenu cur || cur != shop)
            return false;
        int idx = FindShopItem(cur, desiredName);
        if (idx < 0)
            return false; // sold out or scrolled away
        ScrollTo(cur, idx);
        var btn = cur.forSaleButtons[idx - cur.currentItemIndex];
        if (cur.heldItem is Item heldC && !heldC.DisplayName.Equals(desiredName, StringComparison.OrdinalIgnoreCase))
            cur.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Escape);
        cur.safetyTimer = 0; // purchases arm a repeat-click guard; clears like the real timer would
        cur.receiveLeftClick(btn.bounds.Center.X, btn.bounds.Center.Y);
        return true;
    }

    private static int FindShopItemExact(ShopMenu shop, string name)
    {
        for (int i = 0; i < shop.forSale.Count; i++)
        {
            if (shop.forSale[i] != null && shop.forSale[i]!.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
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

    private static int FindCarriedExact(string name)
    {
        for (int i = 0; i < Me.Items.Count; i++)
            if (Me.Items[i] != null && Me.Items[i]!.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int FindCarried(string name)
    {
        for (int i = 0; i < Me.Items.Count; i++)
            if (Me.Items[i] != null && Matches(Me.Items[i]!, name))
                return i;
        return -1;
    }

    /// <summary>Whether the item at <paramref name="idx"/> is on the shop's visible page.</summary>
    private static bool IsVisible(ShopMenu shop, int idx)
    {
        int pageSize = Math.Max(1, shop.forSaleButtons.Count);
        return idx >= shop.currentItemIndex && idx < shop.currentItemIndex + pageSize;
    }

    /// <summary>One scroll step toward the item, like a player clicking the shop's arrow.
    /// Returns whether the list moved.</summary>
    private static bool ScrollStep(ShopMenu shop, int idx)
    {
        if (IsVisible(shop, idx))
            return false;
        if (idx < shop.currentItemIndex)
        {
            if (shop.currentItemIndex <= 0)
                return false;
            Reflection.GetMethod(shop, "upArrowPressed").Invoke();
            Game1.playSound("shwip");
        }
        else
        {
            if (shop.currentItemIndex >= Math.Max(0, shop.forSale.Count - shop.forSaleButtons.Count))
                return false;
            Reflection.GetMethod(shop, "downArrowPressed").Invoke();
            Game1.playSound("shwip");
        }
        return true;
    }

    /// <summary>Scroll the list to the item (one row per step - no warping). Sync, for the CLI path.</summary>
    private static void ScrollTo(ShopMenu shop, int idx)
    {
        for (int i = 0; i < 60 && !IsVisible(shop, idx); i++)
            if (!ScrollStep(shop, idx))
                break;
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
            for (; made < want; made++)
            {
                int r = TryCraftOne(page, comp);
                if (r == 0)
                    break; // no ingredients or the cursor is stuck
            }
            return made > 0
                ? $"Crafted {made} {recipe.DisplayName}{((Game1.activeClickableMenu is GameMenu gm && gm.pages.ElementAtOrDefault(gm.currentTab) is CraftingPage cp && cp.heldItem != null) ? " - one is on your cursor; click=bag when you have room" : "")}."
                : $"Clicked {recipe.DisplayName} - you're missing ingredients; read the recipe list.";
        }
        return $"no recipe '{name}' on this page - click=\"next\" to scroll";
    }

    /// <summary>One craft: the recipe click (item onto the cursor) plus clicking it into a bag
    /// slot. Returns 0 if nothing was crafted (no ingredients / cursor stuck).</summary>
    private static int TryCraftOne(CraftingPage page, ClickableTextureComponent comp)
    {
        if (!IsCurrentPage(page) || page.heldItem != null && !DepositHeld(page))
            return 0; // cursor is stuck full - deal with it first
        page.receiveLeftClick(comp.bounds.Center.X, comp.bounds.Center.Y); // vanilla: item appears on the cursor
        if (page.heldItem == null)
            return 0; // no ingredients
        DepositHeld(page);
        return 1;
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

    // ---------------------------------------------------------------- cooking (the kitchen stove)

    /// <summary>The ingredients a cooking page can draw from besides the backpack: the kitchen's
    /// fridge and any mini-fridges (the page was given them at open time).</summary>
    private static List<Item> FridgeContents(CraftingPage page) => page._materialContainers == null
        ? new List<Item>()
        : page._materialContainers.SelectMany(c => (IList<Item>)c).Where(i => i != null).ToList();

    /// <summary>A standalone CraftingPage: the kitchen stove's cooking menu (cooking recipes, fridge
    /// ingredients) or a workbench (crafting). Same page internals as the backpack's craft tab, so
    /// the actions are the same - the agent just uses "cook" / "craft".</summary>
    private static string Page(CraftingPage page)
    {
        bool cook = page.cooking;
        var sb = new StringBuilder(cook
            ? "Cooking menu (the kitchen - fridge and mini-fridge ingredients count)"
            : "Crafting menu (workbench)");
        var recipes = page.pagesOfCraftingRecipes.ElementAtOrDefault(page.currentCraftingPage);
        if (recipes is { Count: > 0 })
        {
            var fridge = cook ? FridgeContents(page) : null;
            foreach (var (comp, recipe) in recipes)
            {
                string parts = string.Join(", ", recipe.recipeList.Select(kv => IngredientForCraft(kv)));
                bool ghosted = cook && comp.hoverText == "ghosted"; // recipe not learned yet
                sb.Append($"\n- {(ghosted ? "(recipe not learned) " : "")}{recipe.DisplayName}: {parts}"
                    + (recipe.doesFarmerHaveIngredientsInInventory(fridge) ? " (you have the ingredients)" : " (missing some)"));
            }
        }
        if (cook && FridgeContents(page) is { Count: > 0 } inFridge)
            sb.Append("\nFridge: " + string.Join(", ", inFridge.Select(i => i.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName)));
        if (page.heldItem != null)
            sb.Append($"\n(you're holding {page.heldItem.DisplayName} x{page.heldItem.Stack} on the cursor - click=\"bag\" to put it in a free slot)");
        if (page.pagesOfCraftingRecipes.Count > 1)
            sb.Append($"\n(page {page.currentCraftingPage + 1} of {page.pagesOfCraftingRecipes.Count} - click=\"next\"/\"prev\")");
        sb.Append($"\nActions: click=\"{(cook ? "cook" : "craft")} {{name}}\" (n=N for N; each is also clicked into a bag slot for you), "
            + "click=\"bag\", click=\"next\"/\"prev\", click=\"close\".");
        return sb.ToString();
    }

    private static string PageClick(CraftingPage page, string element, int n)
    {
        string q = element.Trim();
        bool cook = page.cooking;
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
        {
            // Exiting with something on the cursor would lose it - put it away first (the same
            // safety the craft loop has, just for a direct close).
            if (page.heldItem != null && !DepositHeld(page))
                return "you're holding an item - click=\"bag\" when you have room, then close";
            return Close();
        }
        if (q.Equals("bag", StringComparison.OrdinalIgnoreCase))
        {
            if (page.heldItem == null)
                return "your cursor is empty already";
            return DepositHeld(page) ? "Put it in your bag." : "No free slot - make room (store things) and click bag again.";
        }
        // The standalone page's arrow buttons aren't laid out until a frame later, so switch the
        // page directly (same as the vanilla down/up arrow click, just without the button).
        if (q.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            if (page.currentCraftingPage >= Math.Max(0, page.pagesOfCraftingRecipes.Count - 1))
                return "Already at the last page.";
            page.currentCraftingPage++;
            return $"Next {(cook ? "cooking" : "crafting")} page.";
        }
        if (q.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            if (page.currentCraftingPage <= 0)
                return "Already at the first page.";
            page.currentCraftingPage--;
            return $"Previous {(cook ? "cooking" : "crafting")} page.";
        }
        if (q.StartsWith("cook ", StringComparison.OrdinalIgnoreCase))
            q = q["cook ".Length..].Trim();
        else if (q.StartsWith("craft ", StringComparison.OrdinalIgnoreCase))
            q = q["craft ".Length..].Trim();
        var recipes = page.pagesOfCraftingRecipes[page.currentCraftingPage];
        foreach (var (comp, recipe) in recipes)
        {
            if (!recipe.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            int made = 0;
            int want = Math.Clamp(n, 1, 25);
            for (; made < want; made++)
            {
                if (TryCraftOne(page, comp) == 0)
                    break; // no ingredients or the cursor is stuck
            }
            return made > 0
                ? $"{(cook ? "Cooked" : "Crafted")} {made} {recipe.DisplayName}{(page.heldItem != null ? " - one is on your cursor; click=bag when you have room" : "")}."
                : $"Clicked {recipe.DisplayName} - you're missing ingredients{(cook ? " (the fridge counts)" : "")}; read the recipe list.";
        }
        return $"no recipe '{q}' on this page - click=\"next\" to scroll";
    }

    // ---------------------------------------------------------------- animals (Marnie's shop)

    /// <summary>A building's display name, for the 'place in <name>' clicks.</summary>
    private static string BuildingName(StardewValley.Buildings.Building b) =>
        StardewValley.TokenizableStrings.TokenParser.ParseText(b.GetData()?.Name) ?? b.buildingType.Value;

    /// <summary>Type into the current text box (the animal-naming box at Marnie's today).</summary>
    public static string Type(string text)
    {
        if (Game1.activeClickableMenu is not PurchaseAnimalsMenu { namingAnimal: true } menu)
            return "there's no text box to type into right now";
        if (string.IsNullOrWhiteSpace(text))
            return "type a name first: type \"<name>\"";
        menu.textBox.Text = text;
        return $"Typed \"{menu.textBox.Text}\" - click=\"done\" to confirm the purchase.";
    }

    /// <summary>The whole animal shop, as the agent should see it: the price list, then the two
    /// steps after buying one - choosing a building to put it in, then naming it.</summary>
    private static string Animals(PurchaseAnimalsMenu menu)
    {
        var sb = new StringBuilder("[Marnie's ranch - animals]");
        if (menu.namingAnimal)
        {
            sb.Append($"\nYou're naming your {menu.animalBeingPurchased?.displayType ?? "animal"} (it will cost {menu.priceOfAnimal}g).");
            if (menu.textBox.Text.Length > 0)
                sb.Append($"\nCurrent name: {menu.textBox.Text}");
            sb.Append("\nActions: type \"<name>\" to name it, click=\"done\" to buy it, click=\"random\" "
                + "for a random name, click=\"back\" to cancel the purchase.");
            return sb.ToString();
        }
        if (menu.onFarm)
        {
            var animal = menu.animalBeingPurchased;
            sb.Append($"\nChoose a home for your {animal?.displayType ?? "animal"} ({menu.priceOfAnimal}g).");
            bool any = false;
            foreach (var b in Game1.getFarm().buildings)
            {
                if (b.isUnderConstruction() || b.GetIndoors() is not StardewValley.AnimalHouse h)
                    continue;
                any = true;
                bool full = h.isFull();
                bool fits = animal == null || animal.CanLiveIn(b);
                sb.Append($"\n- {BuildingName(b)} at {b.tileX.Value},{b.tileY.Value} "
                    + $"({h.animalsThatLiveHere.Count}/{h.animalLimit.Value} in)"
                    + (full ? " - full" : "")
                    + (fits ? " (click=\"place in " + BuildingName(b) + "\")" : $" - won't hold a {animal!.displayType}"));
            }
            if (!any)
                sb.Append("\nThere's no animal building on the farm yet - build a Coop or a Barn at Robin's first, then come back.");
            sb.Append("\nActions: click=\"place in <building>\", click=\"back\" (cancel).");
            return sb.ToString();
        }

        // The price list (the shop itself).
        sb.Append($" You have {Me.Money}g.");
        int shown = 0;
        foreach (var entry in menu.animalsToPurchase)
        {
            if (entry?.item is not SObject o)
                continue;
            string name = FarmAnimal.GetDisplayName(entry.hoverText, forShop: true);
            int price = o.salePrice();
            if (o.Type != null)
                sb.Append($"\n- {name} ({price}g) - needs {o.Type}"); // greyed out until the requirement is met
            else
                sb.Append($"\n- {name} ({price}g) (click=\"buy {name}\")");
            if (++shown >= 15)
                break;
        }
        if (menu.animalsToPurchase.Count > shown)
            sb.Append($"\n... and {menu.animalsToPurchase.Count - shown} more (click=\"next\"/\"prev\" scrolls)");
        sb.Append("\nActions: click=\"buy <animal>\", click=\"next\"/\"prev\" (scroll), click=\"close\" (the animal "
            + "goes to its new home: you pick the building, then type its name).");
        return sb.ToString();
    }

    private static string AnimalClick(PurchaseAnimalsMenu menu, string element)
    {
        string q = element.Trim();
        if (menu.namingAnimal)
        {
            // Leaving while naming (or on the farm view) must cancel the placement the vanilla way
            // (setUpForReturnToShopMenu restores the HUD/viewport); a bare exit would leave the
            // farm view stuck.
            if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase)
                || q.Equals("back", StringComparison.OrdinalIgnoreCase) || q.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                menu.setUpForReturnToShopMenu();
                return "Cancelled - back in the animal shop; your gold is untouched.";
            }
            if (q.Equals("done", StringComparison.OrdinalIgnoreCase) || q.Equals("ok", StringComparison.OrdinalIgnoreCase)
                || q.Equals("confirm", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(menu.textBox.Text))
                    return "type a name first: type \"<name>\", then click=\"done\".";
                string named = menu.textBox.Text;
                string typ = menu.animalBeingPurchased?.displayType ?? "animal";
                // The vanilla done button: adopt the animal, pay for it, and warp back to Marnie's
                // (her "goodbye" dialogue follows - answer it).
                menu.receiveLeftClick(menu.doneNamingButton.bounds.Center.X, menu.doneNamingButton.bounds.Center.Y);
                return $"Bought the {typ} \"{named}\" for {menu.priceOfAnimal}g (money: {Me.Money}g). Marnie has a message - answer it.";
            }
            if (q.Equals("random", StringComparison.OrdinalIgnoreCase))
            {
                menu.receiveLeftClick(menu.randomButton.bounds.Center.X, menu.randomButton.bounds.Center.Y);
                return $"Random name: {menu.textBox.Text} - click=\"done\" to confirm.";
            }
            return "you're naming the animal: type \"<name>\", then click=\"done\" (or \"random\", \"back\").";
        }

        if (menu.onFarm)
        {
            if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase)
                || q.Equals("back", StringComparison.OrdinalIgnoreCase) || q.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                menu.setUpForReturnToShopMenu();
                return "Cancelled - back in the animal shop.";
            }
            // Choosing the building: center the view on it and click its door tile, exactly like a
            // player moving the mouse over the building and clicking (same screen coordinates).
            if (q.StartsWith("place ", StringComparison.OrdinalIgnoreCase) || q.StartsWith("put ", StringComparison.OrdinalIgnoreCase))
            {
                string name = q;
                if (q.StartsWith("place in ", StringComparison.OrdinalIgnoreCase))
                    name = q["place in ".Length..].Trim();
                else if (q.StartsWith("place ", StringComparison.OrdinalIgnoreCase))
                    name = q["place ".Length..].Trim();
                else if (q.StartsWith("put in ", StringComparison.OrdinalIgnoreCase))
                    name = q["put in ".Length..].Trim();
                else if (q.StartsWith("put ", StringComparison.OrdinalIgnoreCase))
                    name = q["put ".Length..].Trim();
                var building = Game1.getFarm().buildings.FirstOrDefault(b =>
                    !b.isUnderConstruction() && b.GetIndoors() is StardewValley.AnimalHouse h && !h.isFull()
                    && BuildingName(b).Contains(name, StringComparison.OrdinalIgnoreCase));
                if (building == null)
                    return $"no building named '{name}' with room - pick from the list (or build one at Robin's)";
                if (menu.animalBeingPurchased != null && !menu.animalBeingPurchased.CanLiveIn(building))
                    return $"a {menu.animalBeingPurchased.displayType} can't live in {BuildingName(building)} - pick a building from the list";
                var view = menu.GetTopLeftPixelToCenterBuilding(building);
                Game1.viewport.X = view.X; // xTile viewport: center it on the chosen building
                Game1.viewport.Y = view.Y;
                var door = building.humanDoor.Value;
                var tile = new Microsoft.Xna.Framework.Vector2(building.tileX.Value + door.X, building.tileY.Value + door.Y);
                int cx = (int)((tile.X * 64 - Game1.viewport.X) * Game1.options.zoomLevel / Game1.options.uiScale);
                int cy = (int)((tile.Y * 64 - Game1.viewport.Y) * Game1.options.zoomLevel / Game1.options.uiScale);
                Game1.setMousePosition(cx, cy);
                menu.receiveLeftClick(cx, cy);
                if (menu.namingAnimal)
                    return $"It's going into {BuildingName(building)} - name it: type \"<name>\", then click=\"done\".";
                return $"clicked {BuildingName(building)} but it didn't take - try again, or pick from the list";
            }
            if (q.Equals("back", StringComparison.OrdinalIgnoreCase) || q.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                menu.setUpForReturnToShopMenu();
                return "Cancelled - back in the animal shop.";
            }
            return "choose a home: click=\"place in <building>\" from the list (or click=\"back\").";
        }

        // The shop list itself.
        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
            return Close();
        if (q.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            int before = menu.currentScroll;
            menu.Scroll(1);
            return menu.currentScroll > before ? "Scrolled down." : "Already at the bottom.";
        }
        if (q.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            int before = menu.currentScroll;
            menu.Scroll(-1);
            return menu.currentScroll < before ? "Scrolled up." : "Already at the top.";
        }
        if (q.StartsWith("buy ", StringComparison.OrdinalIgnoreCase))
            q = q["buy ".Length..].Trim();
        var salable = menu.animalsToPurchase.FirstOrDefault(a => a?.item is SObject ao && ao.Type == null
            && FarmAnimal.GetDisplayName(a.hoverText, forShop: true).Contains(q, StringComparison.OrdinalIgnoreCase));
        if (salable is not { item: SObject so })
            return menu.animalsToPurchase.Any(a => a?.item is SObject oo && oo.Type == null)
                ? $"the ranch doesn't sell '{q}' - pick from the list"
                : "no animals for sale right now";
        int price = so.salePrice();
        if (Me.Money < price)
            return $"that's {price}g and you have {Me.Money}g - sell crops first (money is shared)";
        // A player clicks the animal: the screen fades to the farm, where they choose a building.
        menu.receiveLeftClick(salable.bounds.Center.X, salable.bounds.Center.Y);
        return $"Picked a {salable.hoverText} ({price}g) - now choose its home: click=\"place in <building>\". "
            + "(click=\"back\" to cancel)";
    }

    // ---------------------------------------------------------------- buildings (Robin's shop)

    /// <summary>Robin's construction menu: the blueprint browser, and the farm view after a build/
    /// demolish/move click. The actions are the vanilla ones - the agent just picks blueprint,
    /// tiles and buttons.</summary>
    private static string Carpentry(CarpenterMenu menu)
    {
        var sb = new StringBuilder("[Robin's carpentry shop]");
        if (menu.onFarm)
        {
            var bp = menu.Blueprint;
            string what = menu.Action switch
            {
                CarpenterMenu.CarpentryAction.Demolish => "Which building do you want to demolish?",
                CarpenterMenu.CarpentryAction.Move => "Which building do you want to move?",
                CarpenterMenu.CarpentryAction.Paint => "which building to paint",
                CarpenterMenu.CarpentryAction.Upgrade => $"Pick the {bp.GetDisplayNameForBuildingToUpgrade() ?? bp.DisplayName} to upgrade it to {bp.DisplayName}",
                _ => bp.IsUpgrade
                    ? $"Pick the {bp.GetDisplayNameForBuildingToUpgrade() ?? "building"} to turn into {bp.DisplayName}"
                    : $"Choose where to put the new {bp.DisplayName} (it costs {bp.BuildCost}g + the materials listed)",
            };
            sb.Append($"\n{what} You're looking at the farm; the buildings are:");
            foreach (var b in menu.TargetLocation.buildings)
                sb.Append($"\n- {BuildingName(b)} at {b.tileX.Value},{b.tileY.Value}");
            // The agent can't see the farm map from in here, so also list a few genuinely clear
            // spots: every tile of the building's footprint must pass the same isBuildable the
            // build click will use (and nothing else prevent construction).
            var farm = menu.TargetLocation;
            var door = Game1.getFarm().GetMainFarmHouseEntry();
            var sample = StardewValley.Buildings.Building.CreateInstanceFromId(menu.Blueprint.Id, Microsoft.Xna.Framework.Vector2.Zero);
            var spots = new List<string>();
            for (int dy = 1; dy < 12 && spots.Count < 6; dy++)
            for (int dx = -6; dx < 40 && spots.Count < 6; dx++)
            {
                var t = new Microsoft.Xna.Framework.Vector2(door.X + dx, door.Y + dy);
                if (t.X < 0 || t.Y < 0)
                    continue;
                bool ok = true;
                for (int y = 0; y < sample.tilesHigh.Value && ok; y++)
                for (int x = 0; x < sample.tilesWide.Value && ok; x++)
                {
                    var ft = t + new Microsoft.Xna.Framework.Vector2(x, y);
                    ok = farm.isBuildable(ft) && farm.getBuildingAt(ft) == null;
                }
                if (ok && sample.isThereAnythingtoPreventConstruction(farm, t) == null)
                    spots.Add($"{t.X},{t.Y}");
            }
            if (spots.Count > 0)
                sb.Append("\nClear spots near the farmhouse: " + string.Join(", ", spots) + " (click=\"tile <x>,<y>\" to build there).");
            else
                sb.Append("\nNo clear spot right by the farmhouse - clear weeds/stones/twigs first (scythe/pickaxe/axe), then look again.");
            sb.Append("\nActions: click=\"tile X,Y\" (a clear spot or a building), click=\"back\" (cancel).");
            return sb.ToString();
        }

        var bp0 = menu.Blueprint;
        sb.Append($"\nBlueprint {bp0.Index + 1} of {menu.Blueprints.Count}: {bp0.DisplayName}"
            + (bp0.IsUpgrade ? $" (upgrade for a {bp0.GetDisplayNameForBuildingToUpgrade() ?? "building"})" : ""));
        var have = new List<string>();
        var lack = new List<string>();
        foreach (var ing in menu.ingredients)
        {
            int count = Me.Items.Sum(i => i != null && i.QualifiedItemId == ing.QualifiedItemId ? i.Stack : 0);
            (count >= ing.Stack ? have : lack).Add($"{ing.Stack} {ing.DisplayName} (have {count})");
        }
        sb.Append($"\nCost: {(bp0.BuildCost > 0 ? $"{bp0.BuildCost}g (you have {Me.Money}g)" : "free")}."
            + (menu.ingredients.Count > 0 ? $" Materials: {string.Join(", ", have.Concat(lack))}." : "")
            + (menu.CanBuildCurrentBlueprint() ? " You can afford it." : " You CAN'T build this yet.")
            + (bp0.BuildDays > 0 ? $"\nTakes {bp0.BuildDays} day(s) to build." : ""));
        sb.Append("\nActions: click=\"next\"/\"prev\" (browse), click=\"select <name>\", click=\"build\" "
            + "(new buildings: then click a spot on the farm; upgrades: then click the building), "
            + "click=\"demolish\" or \"move\" (then click a building on the farm), click=\"close\".");
        return sb.ToString();
    }

    private static string CarpentryClick(CarpenterMenu menu, string element)
    {
        string q = element.Trim();
        if (menu.onFarm)
        {
            if (q.Equals("back", StringComparison.OrdinalIgnoreCase) || q.Equals("cancel", StringComparison.OrdinalIgnoreCase)
                || q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase))
            {
                // The cancel BUTTON is a no-op while a building is picked up (vanilla returns early),
                // so mimic the ESC key, which cancels the pickup and returns to the shop view.
                if (menu.Action == CarpenterMenu.CarpentryAction.Move && menu.buildingToMove != null)
                    menu.returnToCarpentryMenu();
                else
                    menu.receiveLeftClick(menu.cancelButton.bounds.Center.X, menu.cancelButton.bounds.Center.Y);
                return "Back in Robin's menu (nothing changed).";
            }
            if (q.StartsWith("tile ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = q["tile ".Length..].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int tx) && int.TryParse(parts[1].Trim(), out int ty))
                {
                    // The on-farm clicks read the real mouse position, so click the way a player
                    // does: centre the view on the tile, put the mouse there, then the vanilla
                    // receiveLeftClick performs the build/upgrade/demolish/move.
                    Game1.viewport.X = (int)(tx * 64 - Game1.viewport.Width / 2f);
                    Game1.viewport.Y = (int)(ty * 64 - Game1.viewport.Height / 2f);
                    Game1.clampViewportToGameMap();
                    int mx = tx * 64 - Game1.viewport.X;
                    int my = ty * 64 - Game1.viewport.Y;
                    Game1.setMousePosition(mx, my, ui_scale: false);
                    menu.receiveLeftClick(mx, my);
                    return PlacementOutcome(menu);
                }
                return "say click=\"tile X,Y\" with two numbers and a comma, e.g. click=\"tile 63,13\"";
            }
            return "choose a farm tile: click=\"tile X,Y\" (or click=\"back\" to cancel)";
        }

        if (q.Equals("close", StringComparison.OrdinalIgnoreCase) || q.Equals("leave", StringComparison.OrdinalIgnoreCase)
            || q.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            return Close();
        if (q.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            menu.SetNewActiveBlueprint(menu.Blueprint.Index + 1); // what the vanilla forward button does
            return $"Browsing: {menu.Blueprint.DisplayName}.";
        }
        if (q.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            menu.SetNewActiveBlueprint(menu.Blueprint.Index - 1);
            return $"Browsing: {menu.Blueprint.DisplayName}.";
        }
        if (q.StartsWith("select ", StringComparison.OrdinalIgnoreCase))
        {
            string name = q["select ".Length..].Trim();
            var match = menu.Blueprints.FirstOrDefault(bp =>
                bp.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || bp.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase))
                ?? menu.Blueprints.FirstOrDefault(bp => bp.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                return $"no blueprint '{name}' here - browse with next/prev to see Robin's list";
            menu.SetNewActiveBlueprint(match.Index);
            return $"Selected {menu.Blueprint.DisplayName}" + (menu.Blueprint.IsUpgrade ? " (upgrade)" : "") + " - click=\"build\" to place it.";
        }
        if (q.Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            if (!menu.CanBuildCurrentBlueprint())
                return $"you can't afford the {menu.Blueprint.DisplayName} yet - the cost/materials are in the menu; sell crops or gather more first";
            // Vanilla: the ok button fades to the farm to place the building (or pick the
            // building to upgrade).
            menu.receiveLeftClick(menu.okButton.bounds.Center.X, menu.okButton.bounds.Center.Y);
            return menu.onFarm
                ? $"On the farm: choose where (click=\"tile X,Y\"){(menu.Action == CarpenterMenu.CarpentryAction.Upgrade ? $" - click the {menu.Blueprint.GetDisplayNameForBuildingToUpgrade() ?? "existing building"} to upgrade" : " to put the new " + menu.Blueprint.DisplayName)}"
                : "clicked build but nothing happened - did the requirements change?";
        }
        if (q.Equals("demolish", StringComparison.OrdinalIgnoreCase))
        {
            menu.receiveLeftClick(menu.demolishButton.bounds.Center.X, menu.demolishButton.bounds.Center.Y);
            return menu.onFarm
                ? "Pick the building to demolish: click=\"tile X,Y\" on it."
                : "you can't demolish right now (Robin only demolishes while no building is under construction)";
        }
        if (q.Equals("move", StringComparison.OrdinalIgnoreCase))
        {
            menu.receiveLeftClick(menu.moveButton.bounds.Center.X, menu.moveButton.bounds.Center.Y);
            return menu.onFarm
                ? "Pick the building to move: click=\"tile X,Y\" on it, then where to put it."
                : "you can't move buildings right now (the host decides, or a building is under construction)";
        }
        return "say click=\"next\", \"prev\", \"select <name>\", \"build\", \"demolish\", \"move\", or \"close\"";
    }

    /// <summary>What the on-farm click just started, read from the menu's state after the click.</summary>
    private static string PlacementOutcome(CarpenterMenu menu)
    {
        if (!menu.onFarm || menu.freeze)
            return "Done - Robin wraps it up in a moment (answer her message).";
        if (menu.Action == CarpenterMenu.CarpentryAction.Move && menu.buildingToMove != null)
            return "Picked up that building - now click where to move it: click=\"tile X,Y\" (or back to cancel).";
        if (menu.Action == CarpenterMenu.CarpentryAction.Upgrade)
            return "Clicked the building - if it was the right kind, the upgrade starts and Robin confirms in a moment (answer her).";
        if (menu.Action == CarpenterMenu.CarpentryAction.Demolish)
            return "Clicked the building - if it could be demolished it's gone now (a cabin asks first: answer it).";
        return "Clicked the tile. Check the result: Robin may have a message (answer it), or the buildings list changed.";
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