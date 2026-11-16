namespace NobodyValley;

internal sealed class ModConfig
{
    /// <summary>First TCP port to try on 127.0.0.1. Each game instance binds the first free port from here.</summary>
    public int BasePort { get; set; } = 8765;

    /// <summary>How many ports to try (i.e. max number of instances).</summary>
    public int PortRange { get; set; } = 8;

    /// <summary>
    /// Centre of the shared crop field, relative to the farmhouse door. The agents till outwards from here.
    /// The default puts it a few tiles south of the farmhouse on the standard farm.
    /// </summary>
    public int FieldOffsetX { get; set; } = 0;
    public int FieldOffsetY { get; set; } = 6;

    /// <summary>Jobs stop when energy drops below this.</summary>
    public int MinEnergy { get; set; } = 10;

    /// <summary>
    /// UI scale (0.5-1.5) to use in game; smaller makes the chat box and menus fit the small tiled windows.
    /// Null leaves the game's own setting alone.
    /// </summary>
    public float? UiScale { get; set; } = 0.6f;

    /// <summary>Automatically dismiss end-of-day menus (shipping summary, level ups), dialogue and cutscenes.</summary>
    public bool AutoDismissMenus { get; set; } = true;

    /// <summary>Give each farmer a random look when the save loads (the same one every time, per name).</summary>
    public bool RandomLooks { get; set; } = true;

    /// <summary>Change this to roll different random looks.</summary>
    public int LookSeed { get; set; } = 0;

    /// <summary>How long such a screen stays up before it's dismissed (so spectators can see it).</summary>
    public float MenuDisplaySeconds { get; set; } = 5f;
}
