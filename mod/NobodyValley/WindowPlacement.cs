using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace NobodyValley;

/// <summary>
/// Put the game in a window at a given position/size, so a launcher can tile several instances:
///   NOBODYVALLEY_WINDOW=x,y,width,height   (screen points; e.g. 0,25,1512,957)
/// </summary>
internal static class WindowPlacement
{
    public static void Apply(IMonitor monitor)
    {
        var spec = Environment.GetEnvironmentVariable("NOBODYVALLEY_WINDOW");
        if (string.IsNullOrWhiteSpace(spec))
            return;
        var parts = spec.Split(',').Select(p => int.TryParse(p.Trim(), out var v) ? v : (int?)null).ToArray();
        if (parts.Length != 4 || parts.Any(p => p == null))
        {
            monitor.Log($"Ignoring NOBODYVALLEY_WINDOW='{spec}' (expected x,y,width,height).", LogLevel.Warn);
            return;
        }
        int x = parts[0]!.Value, y = parts[1]!.Value, w = parts[2]!.Value, h = parts[3]!.Value;

        try
        {
            if (Game1.graphics.IsFullScreen || Game1.options.windowedBorderlessFullscreen)
                Game1.options.setWindowedOption("Windowed");
            Game1.game1.Window.AllowUserResizing = true;
            Game1.game1.SetWindowSize(w, h);
            Game1.game1.Window.Position = new Point(x, y);
            monitor.Log($"Window placed at {x},{y} size {w}x{h}.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't place the window: {ex.Message}", LogLevel.Warn);
        }
    }
}
