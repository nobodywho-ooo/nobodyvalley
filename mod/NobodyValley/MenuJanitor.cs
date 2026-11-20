using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Dismisses only the screens that can't wait for an agent turn: the overnight shipping summary and
/// cutscenes (plus the day-end ready check, which confirms itself). Everything interactive - shops,
/// chests, dialogues, letters, level-ups - stays on screen for the agent to read and click, like a
/// player would. Each dismissed screen stays up a few seconds first, so spectators get to see it.
/// </summary>
internal sealed class MenuJanitor
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Func<int> displayTicks;
    private readonly Func<bool>? holdScreen;

    /// <summary>The screen we're currently timing, and how long it's been up.</summary>
    private object? current;
    private int ticksShown;

    public MenuJanitor(IModHelper helper, IMonitor monitor, Func<int> displayTicks, Func<bool>? holdScreen = null)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.displayTicks = displayTicks;
        this.holdScreen = holdScreen;
    }

    /// <param name="leaveDialoguesAlone">A job is reading/answering dialogue boxes itself.</param>
    public void Tick(bool leaveDialoguesAlone = false)
    {
        object? screen = Game1.CurrentEvent is { skipped: false } ev ? ev : Game1.activeClickableMenu;
        if (!ReferenceEquals(screen, current))
        {
            current = screen;
            ticksShown = 0;
        }
        if (screen == null)
            return;
        if (holdScreen?.Invoke() == true && screen is DialogueBox or Event or ReadyCheckDialog)
            return;
        // Festivals get a longer look before they're ended (agents can't do anything useful at one).
        int grace = screen is Event { isFestival: true } ? displayTicks() * 12 : displayTicks();
        if (++ticksShown < grace)
            return;

        bool acted = screen switch
        {
            Event { isFestival: true } festival => EndFestival(festival),
            Event e => SkipEvent(e),
            ShippingMenu shipping => DismissShipping(shipping),
            ReadyCheckDialog { checkName: "sleep" } => LeaveDayEndReadyCheck(),
            ReadyCheckDialog ready => Confirm(ready),
            _ => false, // everything else (shops, chests, dialogues, letters, level-ups) is the agent's to click
        };
        if (acted)
            ticksShown = 0; // e.g. the next page of a dialogue gets its own few seconds
    }

    // A festival freezes everyone on its map and the agents can't meaningfully take part, so once
    // it's been shown for a little while, end it for this player (vanilla warps them back home).
    private static bool EndFestival(Event ev)
    {
        ev.forceEndFestival(Game1.player);
        return true;
    }

    // The day-end "sleep" ready check must NOT be force-confirmed, and there's no need to:
    // ReadyCheckDialog.update() marks the local player ready every tick and confirms itself the
    // moment every required farmer is ready. That self-confirm is the coordinated multiplayer
    // handshake - all machines run doSleep()/NewDay() together, then sync the new day. Force-
    // confirming early (or via `answer`) drops one machine out of the handshake: it starts its
    // new-day transition on its own and the whole game deadlocks at 2 AM (farmhands frozen
    // "waiting for the other players", the day never rolls).
    private static bool LeaveDayEndReadyCheck()
    {
        return false;
    }

    // Other ready checks (festival start/end, group events) confirm themselves the same way once
    // everyone's ready; confirming them early is harmless, but for symmetry we leave the vanilla
    // flow alone wherever possible.
    private static bool Confirm(ReadyCheckDialog dialog)
    {
        dialog.confirm();
        return true;
    }

    private bool SkipEvent(Event ev)
    {
        // Even "unskippable" cutscenes; nobody's there to click through them. Festivals are left alone.
        monitor.Log($"Skipping cutscene '{ev.id}'.", LogLevel.Info);
        ev.skipped = true; // like the vanilla skip button, so we only skip once
        ev.skipEvent();
        return true;
    }

    private bool DismissShipping(ShippingMenu shipping)
    {
        // Only "click" once it accepts input (intro done, not already fading out): every
        // okClicked() call restarts the fade-out, so clicking repeatedly would hang it forever.
        if (!shipping.CanReceiveInput())
            return false;
        monitor.Log("Dismissing the shipping summary.", LogLevel.Debug);
        helper.Reflection.GetMethod(shipping, "okClicked").Invoke();
        return true;
    }

    private bool DismissLevelUp(LevelUpMenu menu) // kept for toolbar/tests; unused by Tick now
    {
        // This menu ignores receiveLeftClick (it polls the mouse itself), so do what the buttons do.
        if (!menu.readyToClose() || !menu.isActive)
            return false;
        if (menu.isProfessionChooser)
        {
            var choices = helper.Reflection.GetField<List<int>>(menu, "professionsToChoose").GetValue();
            if (choices.Count == 0)
                return false;
            Game1.player.professions.Add(choices[0]); // just take the left one
            menu.getImmediateProfessionPerk(choices[0]);
            menu.isActive = false;
            menu.informationUp = false;
            menu.RemoveLevelFromLevelList();
            monitor.Log($"Level up: picked profession {choices[0]}.", LogLevel.Info);
        }
        else
        {
            menu.okButtonClicked();
            monitor.Log("Level up acknowledged.", LogLevel.Info);
        }
        return true;
    }

    private static bool Click(DialogueBox box)
    {
        box.receiveLeftClick(0, 0);
        return true;
    }

    private static bool Exit(IClickableMenu menu)
    {
        menu.exitThisMenu();
        return true;
    }
}
