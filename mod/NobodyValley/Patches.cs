using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Audio;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Mute all game audio on farmhand instances. Every sound (SFX and music) is an XACT cue whose
/// volume is applied through its category; the game re-applies the Music/Ambient category volumes
/// every tick (fading toward the options), so a one-shot mute would be undone — this forces the
/// requested volume to zero on any category the game adjusts while running as a non-host.
/// </summary>
[HarmonyPatch(typeof(AudioCategoryWrapper), nameof(AudioCategoryWrapper.SetVolume))]
internal static class MuteFarmhandAudioPatch
{
    private static void Prefix(ref float volume)
    {
        try
        {
            if (volume != 0f && ModEntry.Instance.Config.DisableSoundForFarmhands
                && Context.IsWorldReady && !Context.IsMainPlayer)
                volume = 0f;
        }
        catch (Exception ex)
        {
            ModEntry.Instance.Monitor.Log($"Mute audio hook failed: {ex}", StardewModdingAPI.LogLevel.Error);
        }
    }
}

/// <summary>Forward HUD messages (waiting, locked doors, quest hints, ...) to the agent.</summary>
[HarmonyPatch(typeof(Game1), nameof(Game1.addHUDMessage))]
internal static class HudMessagePatch
{
    private static void Postfix(HUDMessage message)
    {
        try { ModEntry.Instance.OnHudMessage(message); }
        catch (Exception ex) { ModEntry.Instance.Monitor.Log($"HUD message hook failed: {ex}", StardewModdingAPI.LogLevel.Error); }
    }
}

/// <summary>Forward direct dialogue text (events and other non-DialogueBox messages) to the agent.</summary>
[HarmonyPatch(typeof(Game1), nameof(Game1.drawDialogueBox), new[] { typeof(string) })]
internal static class RawDialoguePatch
{
    private static void Prefix(string message)
    {
        try { ModEntry.Instance.OnRawScreenText(message); }
        catch (Exception ex) { ModEntry.Instance.Monitor.Log($"Dialogue hook failed: {ex}", StardewModdingAPI.LogLevel.Error); }
    }
}

/// <summary>Forward chat messages from other players to the agent as events.</summary>
[HarmonyPatch(typeof(ChatBox), nameof(ChatBox.receiveChatMessage))]
internal static class ChatPatch
{
    // chatKind: 0 = public, 3 = private message. (1/2 are system notifications.)
    private static void Postfix(long sourceFarmer, int chatKind, string message)
    {
        try
        {
            if (sourceFarmer == Game1.player?.UniqueMultiplayerID || chatKind is not (0 or 3))
                return;
            var from = Game1.GetPlayer(sourceFarmer)?.Name ?? "someone";
            ModEntry.Instance.OnChat(from, message, chatKind == 3);
        }
        catch (Exception ex)
        {
            ModEntry.Instance.Monitor.Log($"Chat hook failed: {ex}", StardewModdingAPI.LogLevel.Error);
        }
    }
}
