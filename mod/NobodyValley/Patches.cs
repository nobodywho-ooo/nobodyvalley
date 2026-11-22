using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Audio;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Mute the background music (and ambient background noise) on farmhand instances, but keep the
/// game's short sound effects (tool swings, level-ups, menu clicks) alongside the footsteps on
/// every instance, so the four tiled windows together still sound alive. Every sound (SFX and
/// music) is an XACT cue whose volume is applied through its category; the game re-applies these
/// category volumes every tick (fading toward the options), so a one-shot mute would be undone —
/// this forces the requested volume to zero on just the background categories while running as a
/// non-host.
/// </summary>
[HarmonyPatch(typeof(AudioCategoryWrapper), nameof(AudioCategoryWrapper.SetVolume))]
internal static class MuteFarmhandMusicPatch
{
    private static readonly System.Reflection.FieldInfo audioCategoryField =
        typeof(AudioCategoryWrapper).GetField("audioCategory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    // The Ambient category is the streaming background noise (wind, birds, the farm ambience), so
    // it counts as "background" too; Sound and Footsteps are the gameplay cues we keep everywhere.
    private static bool IsBackground(AudioCategoryWrapper wrapper) =>
        audioCategoryField.GetValue(wrapper) is Microsoft.Xna.Framework.Audio.AudioCategory c && c.Name is "Music" or "Ambient";

    private static void Prefix(AudioCategoryWrapper __instance, ref float volume)
    {
        try
        {
            if (volume != 0f && ModEntry.Instance.Config.MuteMusicForFarmhands
                && Context.IsWorldReady && !Context.IsMainPlayer && IsBackground(__instance))
                volume = 0f;
        }
        catch (Exception ex)
        {
            ModEntry.Instance.Monitor.Log($"Mute music hook failed: {ex}", StardewModdingAPI.LogLevel.Error);
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

/// <summary>Once an agent has committed to going to bed (answered the "Go to sleep for the
/// night?" question), the "waiting for the other players" ready check must not offer a cancel
/// button. The agent is in bed and can't back out; the day rolls over on its own once every
/// farmer is ready (ReadyCheckDialog.update auto-confirms when all are ready).</summary>
[HarmonyPatch(typeof(ReadyCheckDialog), nameof(ReadyCheckDialog.isCancelable))]
internal static class SleepReadyCheckNotCancelablePatch
{
    private static void Postfix(ReadyCheckDialog __instance, ref bool __result)
    {
        if (__instance.checkName == "sleep")
            __result = false;
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
