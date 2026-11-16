using HarmonyLib;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

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
