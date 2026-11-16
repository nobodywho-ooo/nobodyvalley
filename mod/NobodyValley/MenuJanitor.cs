using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Nobody is at the keyboard, so click through the menus that would otherwise block an agent forever:
/// the overnight shipping summary, level-ups, plain dialogue boxes and cutscenes.
/// </summary>
internal sealed class MenuJanitor
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private int cooldown;
    private int eventTicks;

    public MenuJanitor(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
    }

    /// <param name="leaveDialoguesAlone">A job is reading/answering dialogue boxes itself.</param>
    public void Tick(bool leaveDialoguesAlone = false)
    {
        if (cooldown-- > 0)
            return;
        cooldown = 30;

        // Cutscenes: nobody is there to click through them, so skip them (even "unskippable" ones) after a
        // moment. Festivals are events too, but they're left alone.
        if (Game1.CurrentEvent is { isFestival: false } ev && !ev.skipped)
        {
            if (++eventTicks >= 6) // ~3 seconds
            {
                monitor.Log($"Skipping cutscene '{ev.id}'.", LogLevel.Info);
                ev.skipped = true; // like the vanilla skip button, so we only skip once
                ev.skipEvent();
                eventTicks = 0;
            }
            return;
        }
        eventTicks = 0;

        switch (Game1.activeClickableMenu)
        {
            case ShippingMenu shipping:
                // Only "click" once it accepts input (intro done, not already fading out): every
                // okClicked() call restarts the fade-out, so clicking repeatedly would hang it forever.
                if (shipping.CanReceiveInput())
                {
                    monitor.Log("Dismissing the shipping summary.", LogLevel.Debug);
                    helper.Reflection.GetMethod(shipping, "okClicked").Invoke();
                }
                break;

            case LevelUpMenu levelUp:
                // Profession choice: just take the left one.
                var target = levelUp.isProfessionChooser ? levelUp.leftProfession?.bounds : levelUp.okButton?.bounds;
                if (target is { } b)
                    levelUp.receiveLeftClick(b.Center.X, b.Center.Y);
                break;

            case DialogueBox { isQuestion: false } box when !leaveDialoguesAlone:
                box.receiveLeftClick(0, 0);
                break;

            case LetterViewerMenu letter:
                letter.exitThisMenu();
                break;
        }
    }
}
