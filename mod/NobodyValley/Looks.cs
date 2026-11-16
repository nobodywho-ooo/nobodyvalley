using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Give each farmer a random look (hair, skin, eyes, shirt, pants colour, accessory), using the dice button of the
/// game's own character customisation menu, so the combinations look like the game would pick them.
/// The random seed comes from the farmer's name (plus <see cref="ModConfig.LookSeed"/>), so a farmer keeps
/// the same look across restarts, even though the game only saves overnight.
/// </summary>
internal static class Looks
{
    public static void Randomize(IMonitor monitor, int salt)
    {
        var me = Game1.player;
        var saved = Game1.random;
        Game1.random = new Random(StableHash($"{me.Name}/{salt}"));
        try
        {
            // The new-farmhand menu's dice also rolls the shirt and pants colour (the Wizard's only the body).
            // It only changes the look, not the name, farm, gender, ...
            var menu = new CharacterCustomization(CharacterCustomization.Source.NewFarmhand);
            var button = menu.randomButton.bounds;
            menu.receiveLeftClick(button.Center.X, button.Center.Y, playSound: false);
            // The dice sets a "base" shirt, which the worn shirt hides; wear it instead, like a new farmer does.
            me.ConvertClothingOverrideToClothesItems();
            monitor.Log($"Randomized {me.Name}'s look (hair {me.hair.Value}, skin {me.skin.Value}, shirt {me.shirtItem.Value?.ItemId}).", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't randomize the look: {ex.Message}", LogLevel.Warn);
        }
        finally
        {
            Game1.random = saved;
        }
    }

    /// <summary>FNV-1a; string.GetHashCode() differs between runs.</summary>
    private static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
            h = (h ^ c) * 16777619;
        return (int)h;
    }
}
