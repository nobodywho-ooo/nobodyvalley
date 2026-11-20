using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Watches dialogue boxes, event text and HUD messages, sends them to the agent, and briefly holds
/// the auto-dismiss so the model gets a chance to answer or react instead of losing them after 5s.
/// </summary>
internal sealed class ScreenMessages
{
    private readonly BridgeServer server;
    private readonly IMonitor monitor;
    private DateTime holdUntil = DateTime.MinValue;
    private string lastKey = "";
    private DateTime lastAt = DateTime.MinValue;
    private DialogueBox? lastDialogueBox;
    private string? lastDialogueKey;
    private string? lastEventKey;

    public ScreenMessages(BridgeServer server, IMonitor monitor)
    {
        this.server = server;
        this.monitor = monitor;
    }

    /// <summary>True while a message is waiting for the agent (MenuJanitor leaves the screen alone).</summary>
    public bool Holding => DateTime.UtcNow < holdUntil;

    public void ClearHold() => holdUntil = DateTime.MinValue;

    public void Tick()
    {
        if (!Context.IsWorldReady)
            return;

        if (Game1.activeClickableMenu is DialogueBox box)
        {
            if (!ReferenceEquals(box, lastDialogueBox))
            {
                lastDialogueBox = box;
                lastDialogueKey = null;
            }
            var text = box.getCurrentString() ?? "";
            var options = box.isQuestion
                ? box.responses.Select(r => r.responseText).ToArray()
                : Array.Empty<string>();
            string? speaker = box.characterDialogue?.speaker?.displayName;
            string key = $"box:{RuntimeHelpers.GetHashCode(box)}:{text}:{string.Join("|", options)}";
            if (key == lastDialogueKey)
                return;
            lastDialogueKey = key;
            Report(text, "dialogue", speaker, options, hold: true, dedupeKey: key, dedupeSeconds: 600);
            return;
        }
        lastDialogueBox = null;
        lastDialogueKey = null;

        // An open letter (mail) needs a click to move on.
        if (Game1.activeClickableMenu is LetterViewerMenu letter)
        {
            string text = (letter.mailTitle ?? "a letter") + "\n" + string.Join("\n", letter.mailMessage);
            Report(text, "letter", null, Array.Empty<string>(), hold: true,
                dedupeKey: "letter:" + RuntimeHelpers.GetHashCode(letter), dedupeSeconds: 600);
            return;
        }

        // The multiplayer day-end ready check ("waiting for the other players") freezes the day
        // until everyone is ready, then rolls it over by itself (ReadyCheckDialog.update confirms
        // when every farmer is ready). There's nothing to answer - confirming manually would break
        // the coordinated handshake and deadlock the co-op - so just tell the agent to wait.
        if (Game1.activeClickableMenu is ReadyCheckDialog readyCheck)
        {
            Report("The day is ending: waiting for the other players (ready check). The new day starts on its own once "
                    + "everyone is ready - no action needed.",
                "menu", null, Array.Empty<string>(), hold: true,
                dedupeKey: "ready:" + RuntimeHelpers.GetHashCode(readyCheck), dedupeSeconds: 600);
            return;
        }

        if (Game1.CurrentEvent != null && Game1.currentSpeaker?.CurrentDialogue.Count > 0)
        {
            var dialogue = Game1.currentSpeaker.CurrentDialogue.Peek();
            string text = dialogue.getCurrentDialogue() ?? "";
            var options = dialogue.isCurrentDialogueAQuestion()
                ? dialogue.getNPCResponseOptions().Select(r => r.responseText).ToArray()
                : Array.Empty<string>();
            string key = $"event:{Game1.CurrentEvent.id}:{Game1.currentSpeaker.displayName}:{text}:{string.Join("|", options)}";
            if (key == lastEventKey)
                return;
            lastEventKey = key;
            Report(text, "event", Game1.currentSpeaker.displayName, options, hold: true, dedupeKey: key, dedupeSeconds: 600);
        }
    }

    public void Hud(HUDMessage message)
    {
        // Item-gained messages are noise; everything else may matter (locked door, waiting, quest, ...).
        if (message.messageSubject != null)
            return;
        Report(message.message, "hud", null, Array.Empty<string>(), hold: false);
    }

    public void Raw(string text)
    {
        string key = "raw:" + text;
        Report(text, "event", null, Array.Empty<string>(), hold: true, dedupeKey: key, dedupeSeconds: 60);
    }

    private void Report(string text, string kind, string? speaker, string[] options, bool hold,
        string? dedupeKey = null, double dedupeSeconds = 3)
    {
        text = (text ?? "").Replace("\r", "").Trim();
        if (text.Length == 0)
            return;
        if (text.Length > 600)
            text = text[..600] + "…";

        // The same box is drawn every frame. Ignore the same message within a few seconds, but
        // report it again if the page/text changes or the game repeats it later.
        string key = dedupeKey ?? text + "\n" + string.Join("|", options);
        var now = DateTime.UtcNow;
        if (key == lastKey && (now - lastAt).TotalSeconds < dedupeSeconds)
            return;
        lastKey = key;
        lastAt = now;

        if (hold)
            holdUntil = now.AddSeconds(Math.Max(5, ModEntry.Instance.Config.MessageReactionSeconds));

        var payload = new JObject
        {
            ["text"] = text,
            ["kind"] = kind,
            ["speaker"] = speaker,
            ["options"] = new JArray(options),
        };
        server.Broadcast("screen_message", payload);
        monitor.Log($"Screen message ({kind}): {text}", LogLevel.Debug);
    }
}
