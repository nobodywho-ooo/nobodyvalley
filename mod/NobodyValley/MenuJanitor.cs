using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Nobody is at the keyboard, so click through the screens that would otherwise block an agent forever:
/// the overnight shipping summary, level-ups, letters, plain dialogue boxes and cutscenes.
/// Each one stays up for a few seconds first, so spectators get to see it.
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
            LevelUpMenu levelUp => DismissLevelUp(levelUp),
            DialogueBox { isQuestion: false } box when !leaveDialoguesAlone => Click(box),
            LetterViewerMenu letter => Exit(letter),
            ReadyCheckDialog ready => Confirm(ready),
            _ => false,
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

    // The day-end ready check: nobody's at the keyboard, so confirm it (like its "Ready" button)
    // once it's been shown long enough, or the co-op day hangs forever on "waiting for players".
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

    private bool DismissLevelUp(LevelUpMenu menu)
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
