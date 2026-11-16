using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;

namespace NobodyValley;

/// <summary>
/// Skips the title screen so a launcher script can bring up a whole co-op session unattended:
///   NOBODYVALLEY_HOST=&lt;save folder&gt;   load that save and host it as a co-op game
///   NOBODYVALLEY_LOAD=&lt;save folder&gt;   load that save in single-player (for testing)
///   NOBODYVALLEY_JOIN=&lt;farmer name&gt;   join a LAN game (NOBODYVALLEY_ADDRESS, default localhost) as that farmhand
/// </summary>
internal sealed class AutoStart
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string? hostSave = Environment.GetEnvironmentVariable("NOBODYVALLEY_HOST");
    private readonly string? soloSave = Environment.GetEnvironmentVariable("NOBODYVALLEY_LOAD");
    private readonly string? joinAs = Environment.GetEnvironmentVariable("NOBODYVALLEY_JOIN");
    private readonly string address = Environment.GetEnvironmentVariable("NOBODYVALLEY_ADDRESS") ?? "localhost";

    private bool done;
    private int ticksOnTitle;
    private int ticksInFarmhandMenu;

    public AutoStart(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
        if (hostSave == null && soloSave == null && joinAs == null)
            done = true;
    }

    public void Tick()
    {
        if (done || Context.IsWorldReady)
        {
            done = true;
            return;
        }
        if (Game1.activeClickableMenu is not TitleMenu)
            return;

        // Give the title screen a moment to finish loading.
        if (++ticksOnTitle < 120)
            return;

        if ((hostSave ?? soloSave) is { } save)
        {
            monitor.Log($"Auto-loading save '{save}' ({(hostSave != null ? "co-op host" : "single-player")}).", LogLevel.Info);
            done = true;
            Game1.multiplayerMode = (byte)(hostSave != null ? 2 : 0);
            SaveGame.Load(save);
            Game1.exitActiveMenu();
            return;
        }

        if (TitleMenu.subMenu is not FarmhandMenu menu)
        {
            monitor.Log($"Auto-joining {address} as '{joinAs}'.", LogLevel.Info);
            // Game1.multiplayer is protected internal.
            var multiplayer = helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
            TitleMenu.subMenu = new FarmhandMenu(multiplayer.InitClient(new LidgrenClient(address)));
            ticksInFarmhandMenu = 0;
            return;
        }

        ticksInFarmhandMenu++;
        var slots = helper.Reflection.GetField<List<LoadGameMenu.MenuSlot>>(menu, "menuSlots").GetValue();
        var slot = slots.OfType<FarmhandMenu.FarmhandSlot>()
            .FirstOrDefault(s => s.Farmer.Name.Equals(joinAs, StringComparison.OrdinalIgnoreCase));
        if (slot != null)
        {
            monitor.Log($"Joining as {slot.Farmer.Name}.", LogLevel.Info);
            done = true;
            slot.Activate();
            return;
        }

        // Host not up yet (or slot list not received): retry every 30 seconds.
        if (ticksInFarmhandMenu > 1800)
        {
            monitor.Log($"Didn't find farmhand '{joinAs}' (gettingFarmhands={menu.gettingFarmhands}, approving={menu.approvingFarmhand}, "
                + $"client={(menu.client == null ? "none" : menu.client.timedOut ? "timed out" : "connected")}, slots: "
                + $"{string.Join(", ", slots.OfType<FarmhandMenu.FarmhandSlot>().Select(s => s.Farmer.Name + (s.BelongsToAnotherPlayer() ? " (taken)" : "")))}); retrying.", LogLevel.Warn);
            menu.client?.disconnect();
            TitleMenu.subMenu = null;
        }
    }
}
