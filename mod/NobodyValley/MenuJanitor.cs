using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Dismisses only the screens that can't wait for an agent turn:
/// - the overnight shipping summary (without a click, the co-op day-end stalls forever - the
///   agents are asleep and take no turns until morning);
/// - level-ups: they appear during the sleep fade on the player who levelled; every other
///   machine waits on that player in the co-op day-end handshake, so leaving the screen to the
///   agent to click deadlocks the whole co-op;
/// - festivals, which trap every agent on the festival map for the whole day (ending one is the
///   same as a player leaving it).
/// Cutscenes PLAY: the agent watches them through the event dialogue that ScreenMessages relays,
/// and answers any question with `answer n`. Shops, chests, dialogues and letters are still the
/// agent's own clicks. Each dismissed screen stays up a few seconds first, so spectators see it.
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
            ShippingMenu shipping => DismissShipping(shipping),
            LevelUpMenu level => DismissLevelUp(level),
            ReadyCheckDialog { checkName: "sleep" } => LeaveDayEndReadyCheck(),
            ReadyCheckDialog ready => Confirm(ready),
            // Everything else - cutscenes, shops, chests, dialogues, letters - is either for the
            // agent to watch/click or plays on its own.
            _ => false,
        };
        if (acted)
            ticksShown = 0;
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
    // handshake - all machines run doSleep()/NewDay() together. Force-confirming early drops one
    // machine out of the handshake and deadlocks the whole co-op at 2 AM.
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

    private bool DismissLevelUp(LevelUpMenu level)
    {
        // The menu polls the mouse and doesn't accept clicks during its intro; wait until the
        // OK button / profession split is live (LevelUpMenu.CanReceiveInput). A level 5/10 screen
        // asks the player to pick a profession - pick one (the left option) so the day-end isn't
        // held up; a plain level-up just needs its OK clicked.
        if (!level.CanReceiveInput())
            return false;
        if (level.isProfessionChooser)
        {
            // First profession of the current skill (left side): e.g. Miner for Mining before
            // Geologist. Duplicating LevelUpMenu's own pick-left code path.
            var choices = helper.Reflection.GetField<List<int>>(level, "professionsToChoose").GetValue();
            if (choices == null || choices.Count == 0)
                return false;
            var choice = choices[0];
            monitor.Log($"Picking the {PlayerProfessionName(choice)} profession automatically.", LogLevel.Debug);
            Game1.player.professions.Add(choice);
            level.getImmediateProfessionPerk(choice);
        }
        level.okButtonClicked();
        return true;
    }

    private static string PlayerProfessionName(int profession)
    {
        return Game1.content.LoadString("Strings\\UI:LevelUp_ProfessionName_" + profession switch
        {
            0 => "Rancher", 1 => "Tiller", 2 => "Coopmaster", 3 => "Shepherd", 4 => "Artisan", 5 => "Agriculturist",
            6 => "Fisher", 7 => "Trapper", 8 => "Angler", 9 => "Pirate", 10 => "Mariner", 11 => "Luremaster",
            12 => "Forester", 13 => "Gatherer", 14 => "Lumberjack", 15 => "Tapper", 16 => "Botanist", 17 => "Tracker",
            18 => "Miner", 19 => "Geologist", 20 => "Blacksmith", 21 => "Prospector", 22 => "Excavator",
            23 => "Gemologist", 24 => "Fighter", 25 => "Scout", 26 => "Brute", 27 => "Defender", 28 => "Acrobat",
            _ => "?"
        });
    }
}
