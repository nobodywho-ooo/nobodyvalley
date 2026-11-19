"""One LLM-controlled farmer: a NobodyWho chat plus the event loop that decides when it takes a turn.

Each turn is a single generation constrained (by NobodyWho's JSON-schema sampler) to a decision like

    {"thought":"...","actions":[{"do":"chop","x":63,"y":13},{"do":"water","n":5}]}

We used tool calling at first, but small models tend to narrate between tool calls (and hallucinate
the results); a constrained one-shot decision is faster, bounded in length, and always valid.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import sys
import time
from dataclasses import dataclass, field
from typing import Any, Callable

from nobodywho import ChatAsync, ContextShiftOptions, Model, SamplerPresets

from . import prompts
from .bridge import GameConnection, GameError
from .dashboard import Dashboard

log = logging.getLogger(__name__)

EMOTES = ["happy", "sad", "heart", "exclamation", "question", "angry", "laugh", "surprised", "hi", "music", "yes", "no", "sleep"]

@dataclass(frozen=True)
class Spec:
    """How a decision's action maps onto a mod command."""

    cmd: str  # mod command
    target: str | None = None  # mod argument the "target" field maps to
    item: str | None = None  # mod argument the "item" field maps to
    n: bool = False  # takes a count ("n")
    xy: bool = False  # can act on a map tile ("x", "y")
    needs: tuple[str, ...] = ()  # fields that must be present ("target", "item", "xy")


# Most work actions act on one map tile (x,y), or in bulk on the nearest n (or all) things.
ACTIONS: dict[str, Spec] = {
    "go_to": Spec("go_to", target="target", needs=("target",)),
    "walk": Spec("walk", xy=True, needs=("xy",)),
    "use": Spec("use", xy=True, needs=("xy",)),
    "clear": Spec("clear_debris", xy=True, needs=("xy",)),
    "chop": Spec("chop_trees", xy=True, needs=("xy",)),
    "mine": Spec("mine", xy=True, needs=("xy",)),
    "till": Spec("till", xy=True, needs=("xy",)),
    "plant": Spec("plant", item="seed", xy=True, needs=("xy", "item")),
    "water": Spec("water", xy=True, needs=("xy",)),
    "harvest": Spec("harvest", xy=True, needs=("xy",)),
    "ship": Spec("ship", item="item"),
    "donate": Spec("donate", item="item"),
    "cook": Spec("cook", item="item", needs=("item",)),
    "operate": Spec("operate", xy=True),
    "answer": Spec("answer", n=True),
    "check_community_center": Spec("check_community_center"),
    "check_chest": Spec("check_chest"),
    "open_backpack": Spec("open_backpack"),
    "start_crafting": Spec("start_crafting"),
    "check_shop": Spec("check_shop", target="target"),
    "build": Spec("build", item="item", needs=("item",)),
    "buy_animal": Spec("buy_animal", item="item", needs=("item",)),
    "upgrade": Spec("upgrade", item="item", needs=("item",)),
    "care_animals": Spec("care_animals"),
    "plant_tree": Spec("plant_tree", item="item"),
    "fruit": Spec("fruit"),
    "repair": Spec("repair", target="target", needs=("target",)),
    "buy": Spec("buy", item="item", n=True, needs=("item",)),
    "eat": Spec("eat", item="item"),
    "forage": Spec("forage", target="target", n=True),
    "fish": Spec("fish", target="target", n=True),
    "descend": Spec("descend"),
    "craft": Spec("craft", item="item", needs=("item",)),
    "talk_to": Spec("talk_to", target="target", item="item", needs=("target",)),
    "store": Spec("store", item="item"),
    "take": Spec("take", item="item", n=True, needs=("item",)),
    "sleep": Spec("sleep"),
    "stop": Spec("stop"),
}
INFO_ACTIONS = {"check_community_center", "check_chest", "open_backpack", "start_crafting", "check_shop"}

# Don't till more when this much tilled soil is already waiting for seeds.
MAX_EMPTY_TILLED = 30

MAX_ACTIONS_PER_TURN = 10
# "say" and "emote" are actions like the rest ({"do":"say","text":"..."}); this limits the text.
MAX_SAY_LENGTH = 100

DECISION_SCHEMA = {
    "type": "object",
    # Compact JSON: no whitespace, and fields that aren't needed are simply left out (fewer tokens).
    "x-guidance": {"whitespace_flexible": False},
    "properties": {
        "thought": {"type": "string", "maxLength": 160},
        "actions": {
            "type": "array",
            "maxItems": MAX_ACTIONS_PER_TURN,
            "items": {
                "type": "object",
                "properties": {
                    # "say"/"emote" first, to match the prompt's action list (they're not mod commands
                    # queued in the game - they're handled right away in _take_turn).
                    "do": {"enum": ["say", "emote"] + list(ACTIONS)},
                    "text": {"type": "string", "maxLength": MAX_SAY_LENGTH},
                    "emote": {"enum": EMOTES},
                    "target": {"type": "string", "maxLength": 40},
                    "item": {"type": "string", "maxLength": 40},
                    "x": {"type": "integer", "minimum": 0, "maximum": 200},
                    "y": {"type": "integer", "minimum": 0, "maximum": 200},
                    "n": {"type": "integer", "minimum": 1, "maximum": 25},
                },
                "required": ["do"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["thought", "actions"],
    "additionalProperties": False,
}

# How long an idle agent waits before it gets another turn anyway.
IDLE_TURN_SECONDS = 20.0
# Minimum time between two turns of the same agent.
MIN_TURN_GAP_SECONDS = 3.0
# An observe that gets no reply at all (the game's main loop is stuck, or we lost the socket)
# means the instance is unresponsive: back off instead of hammering it, and give up after a few.
OBSERVE_TIMEOUT = 30.0
FAILURES_BEFORE_DEAD = 3
MAX_OBSERVE_BACKOFF = 300.0
# Keep the chat calm: an agent speaks at most this often (unless answering someone who addressed it).
MIN_SAY_GAP_SECONDS = 90.0
DEBUG_TOKENS = bool(os.environ.get("NV_DEBUG_TOKENS"))
DEBUG_TIMING = bool(os.environ.get("NV_DEBUG_TIMING"))


def _canonical_item(name: str) -> str:
    """Names as comparable strings: lowercased, punctuation stripped, trailing plural 's' dropped
    (so 'Parsnip Seed', 'Parsnip Seeds' and 'Parsnip Seeds x4' all line up)."""
    n = re.sub(r"[^a-z0-9]+", "", name.lower())
    return n[:-1] if n.endswith("s") and len(n) > 3 else n


def _carried_items(inventory: list[str]) -> set[str]:
    """What we carry now: the observation lists 'Axe', 'Parsnip Seeds x4', ... as display names
    (a count suffix only appears when the stack is >1)."""
    names = set()
    for entry in inventory:
        name, sep, _ = entry.rpartition(" x")
        if not sep:  # no ' x<count>' suffix: the entry is just the item name
            name = entry
        names.add(_canonical_item(name))
    return names


def _blank_if_none(value: Any) -> str:
    """The action list says "(none = all ...)"; models then write "none", ":none:" or "all" literally."""
    text = str(value or "").strip()
    return "" if text.strip(":*_ ").lower() in ("none", "all", "everything", "any", "nothing", "null", "here") else text


@dataclass
class Action:
    cmd: str
    args: dict[str, Any] = field(default_factory=dict)

    def describe(self) -> str:
        a = ", ".join(f"{v}" for k, v in self.args.items() if k not in ("x", "y"))
        if "x" in self.args:
            a = f"{self.args['x']},{self.args['y']}" + (f", {a}" if a else "")
        return f"{self.cmd}({a})" if a else self.cmd


class Agent:
    def __init__(self, conn: GameConnection, model: Model, persona: str, n_ctx: int, printer: Callable[[str, str, str], None],
                 start_delay: float = 0.0, dashboard: Dashboard | None = None):
        self.dashboard = dashboard
        self.conn = conn
        self.model = model
        self.persona = persona
        self.n_ctx = n_ctx
        self.print = printer  # (agent name, kind, text)
        self.start_delay = start_delay

        self.name = f"port{conn.port}"
        self.others: list[str] = []
        self.chat: ChatAsync | None = None
        # Health/liveness: set by the turn loop; the main monitor (watchdog) acts on it.
        self.dead = False  # game unresponsive beyond recovery; stop taking turns
        self.observe_fail_streak = 0
        self.health = "ok"  # "ok" | "stale" | "dead" - shown on the dashboard
        self.is_host = False  # tells the watchdog never to kill the host's process

        self.queue: list[Action] = []  # actions waiting to be started
        self.busy: Action | None = None  # job currently running in the game
        self.busy_id: int | None = None  # the mod's id for that job
        self.asleep = False  # in bed; no turns until the next morning
        self.inbox: list[str] = []  # things that happened since the last turn
        self.wake = asyncio.Event()
        self.last_turn = 0.0
        self.last_failure: str | None = None
        self.failure_streak = 0
        self.last_say = 0.0
        self.addressed = False  # someone spoke to us by name since our last turn
        self.screen_message: dict[str, Any] | None = None
        self.screen_message_at = 0.0
        self.screen_message_ignored = False
        self.report_turn = False  # just talked to a villager, so allow a summary even during the say gap

    def _set_health(self, health: str, detail: Any = None) -> None:
        self.health = health
        self._dash(health=health, health_detail=str(detail or ""))

    # ------------------------------------------------------------------ lifecycle

    async def run(self) -> None:
        hello = await self.conn.request("hello")
        while not hello.get("world_ready"):
            self.print(self.name, "info", "waiting for a save to be loaded...")
            await self._wait_for_event("world_ready")
            hello = await self.conn.request("hello")
        self.name = hello["farmer"]

        obs = await self.conn.request("observe")
        self.others = [o["name"] for o in obs.get("others", [])]
        self.chat = ChatAsync(
            self.model,
            n_ctx=self.n_ctx,
            system_prompt=self._system_prompt(),
            sampler=SamplerPresets.constrain_with_json_schema(json.dumps(DECISION_SCHEMA)),
            template_variables={"enable_thinking": False},
            context_shift=ContextShiftOptions(keep_first_turns=0, keep_last_turns=4, target=0.6),
        )
        self.print(self.name, "info", f"connected (port {self.conn.port}, {'host' if hello.get('is_host') else 'farmhand'})")
        self.print(self.name, "system", self._system_prompt())

        events = asyncio.create_task(self._event_loop())
        # Stagger the first turns, so later agents see what the earlier ones picked.
        await asyncio.sleep(self.start_delay)
        self.wake.set()
        try:
            await self._turn_loop()
        finally:
            events.cancel()

    def _persona(self) -> str:
        return self.persona

    def _system_prompt(self) -> str:
        return prompts.system_prompt(self.name, self.others, self._persona(), EMOTES)

    def hear(self, speaker: str, text: str, private: bool = False) -> None:
        """A chat message from another player in the game (another agent, or a human typing)."""
        self.inbox.append(f"{speaker} says: \"{text}\"")
        # Only interrupt for messages aimed at us; others are read at the next turn.
        if private or self.name.lower() in text.lower():
            self.addressed = True
            self.wake.set()

    async def _wait_for_event(self, name: str) -> dict[str, Any]:
        while True:
            ev = await self.conn.events.get()
            if ev["event"] == name:
                return ev

    # ------------------------------------------------------------------ events

    async def _event_loop(self) -> None:
        while True:
            ev = await self.conn.events.get()
            kind = ev["event"]
            if kind == "job_done":
                self._on_job_done(ev)
            elif kind == "chat":
                # The game's chat: agents talk to each other in-game now (`say` is sent through the
                # game's chat box), so every message - from another agent or a human player - comes
                # in through the mod's chat event. The mod filters out each farmer's own messages.
                self.hear(ev["from"], ev["text"], bool(ev.get("private")))
            elif kind == "day_started":
                await self._new_day(ev.get("date", "a new day"))
            elif kind == "time":
                self._on_time(ev)
            elif kind == "screen_message":
                self._on_screen_message(ev)

    def _on_screen_message(self, ev: dict[str, Any]) -> None:
        text = str(ev.get("text", "")).strip()
        if not text:
            return
        kind = ev.get("kind", "message")
        speaker = ev.get("speaker")
        options = [str(o) for o in (ev.get("options") or []) if str(o).strip()]
        label = f"On screen ({kind})"
        if speaker:
            label += f" from {speaker}"
        label += f": \"{text}\""
        if options:
            label += " Options: " + "; ".join(f"{i + 1}) {o}" for i, o in enumerate(options))
        self.inbox.append(label)
        self.screen_message = ev
        self.screen_message_at = time.monotonic()
        self.screen_message_ignored = False
        self.addressed = True
        self.wake.set()

    def _on_time(self, ev: dict[str, Any]) -> None:
        t = ev.get("time_raw", 0)
        if self.asleep or (self.busy is not None and self.busy.cmd == "sleep"):
            return
        if t >= 2400:
            # Midnight: don't leave it to the model, or everyone dawdles into the 2am pass-out.
            self.print(self.name, "info", "it's past midnight; sending them to bed")
            self.queue = [Action("sleep")]
            self.busy, self.busy_id = None, None
            asyncio.create_task(self._start_next())
        elif t >= 2300:
            self.inbox.append(f"It's {ev['time']} - late! Go to bed now (sleep), or you'll pass out at 2am.")
            self.wake.set()
        elif self.busy is None and not self.queue:
            self.inbox.append(f"It's {ev['time']}.")
            self.wake.set()

    def _on_job_done(self, ev: dict[str, Any]) -> None:
        if ev.get("job_id") != self.busy_id:
            return  # a job we replaced or stopped ourselves
        self.busy, self.busy_id = None, None
        self.print(self.name, "result", f"{'Done' if ev['ok'] else 'Failed'}: {ev['summary']}")
        self._dash(result=ev["summary"], result_ok=ev["ok"], doing=self.busy.describe() if self.busy else "", plan=self._plan_text())

        if ev["ok"] and ev["action"] == "sleep":
            self.asleep = True
            self.queue.clear()
            return
        if ev["ok"]:
            self.last_failure = None
            self.failure_streak = 0
            if ev["action"] == "talk_to":
                self.report_turn = True  # give the model a chance to summarize the villager's news
                self.inbox.append("If the villager asked for something or said something useful, summarize it for the others with say.")
            # Always report it, also mid-plan: e.g. "harvest: Nothing is ready" explains why the "ship" after it fails.
            self.inbox.append(f"Done {ev['action']}: {ev['summary']}")
            if self.queue:
                asyncio.create_task(self._start_next())
                return
        else:
            failure = f"{ev['action']}: {ev['summary']}"
            self.failure_streak = self.failure_streak + 1 if failure == self.last_failure else 1
            self.last_failure = failure
            summary = ev.get("summary", "")
            note_cause: list[str] = []
            if "there's nothing to" in summary or "no reachable" in summary:
                # The model keeps aiming at a tile that is empty or that it can't see. Give it the
                # concrete escape: work an actual tile from the map's points-of-interest list.
                note_cause.append(" The tile you chose is empty. Re-read the map's Points of interest (they list "
                                  "exact coordinates) and work one of those tiles, or at whole-row actions you can see.")
            elif "too big for your basic tools" in summary:
                note_cause.append(" That needs an upgraded tool you don't have. Leave it and do something your tools "
                                  "can handle, or get bigger stumps/boulders upgraded at Clint's later.")
            elif "inventory is full" in summary:
                note_cause.append(" Make room: store extra items in the shared chest (`store item`, e.g. `store Wood` "
                                  "or `store Stone`), eat something, or ship things you don't need.")
            elif "still needed for a Community Center bundle" in summary:
                item = summary.split(" is still needed")[0].strip()
                note_cause.append(f" {item} is wanted for a bundle so it can't be shipped; if your backpack is full, "
                                  f"free space with `store {item}` or `eat`, then donate it.")
            # A 9B model stuck in a rut repeats the exact same doomed plan for many turns. Once it's
            # clearly looping, tell it plainly to stop (the earlier "failed the same way" note wasn't
            # enough on its own).
            if self.failure_streak >= 3:
                note_cause.append(" You've now repeated this same failing action over and over. STOP repeating it: "
                                  "do a different action this turn - not this one.")
            note = "" + "".join(note_cause)
            if self.queue:
                note += f" (Cancelled the rest of your plan: {', '.join(a.cmd for a in self.queue)}.)"
                self.queue.clear()
            self.inbox.append(f"Failed {failure}{note}")
        self.wake.set()

    async def _new_day(self, date: str) -> None:
        if self.chat is None:
            return
        # Fresh context each morning: reset the full model context (KV cache, temporary
        # observation, old turns). Each turn's observation is a normal message in the chat from
        # now on (no compaction), so the KV cache stays warm across turns; nobodywho's context
        # shift trims the oldest turns if a day ever runs long.
        if DEBUG_TIMING:
            t = time.monotonic()
        await self.chat.reset(self._system_prompt(), [])
        if DEBUG_TIMING:
            print(f"[t] {self.name} reset (new day): {time.monotonic() - t:.3f}s", file=sys.stderr, flush=True)
        self.print(self.name, "system", self._system_prompt())
        self.queue.clear()
        self.busy, self.busy_id = None, None
        self.asleep = False
        # A new day clearly means the game is up and talking again: reset any unresponsive state.
        self.dead = False
        self.observe_fail_streak = 0
        self._set_health("ok")
        self.inbox = [f"Good morning! It's {date}. Plan your day."]
        self.screen_message = None
        self.report_turn = False
        self.wake.set()

    # ------------------------------------------------------------------ turns

    async def _turn_loop(self) -> None:
        while True:
            try:
                await asyncio.wait_for(self.wake.wait(), timeout=IDLE_TURN_SECONDS)
            except asyncio.TimeoutError:
                if self.busy is not None or self.queue or self.asleep:
                    continue
                self.inbox.append("You're idle. What next?")
            self.wake.clear()

            gap = MIN_TURN_GAP_SECONDS - (time.monotonic() - self.last_turn)
            if gap > 0:
                await asyncio.sleep(gap)
            await asyncio.sleep(0.5)  # let simultaneous events pile up into one turn
            self.wake.clear()
            if self.dead:
                # Repeated no-answers: stop taking turns; the watchdog decides what happens next.
                return
            if self.asleep:
                self.inbox.clear()
                continue
            if self.conn.closed.is_set():
                self.print(self.name, "info", "game disconnected")
                return
            await self._take_turn()

    async def _take_turn(self) -> None:
        assert self.chat is not None
        self.last_turn = time.monotonic()
        try:
            obs = await self.conn.request("observe", timeout=OBSERVE_TIMEOUT)
        except GameError as e:
            # The game answered, just not with what we asked - not a health problem.
            self.observe_fail_streak = 0
            self._set_health("ok")
            self.print(self.name, "info", f"observe failed: {e}")
            return
        except asyncio.TimeoutError:
            # No reply at all: the game's main loop is stuck, or we lost the socket. Back off (the
            # watchdog looks at this too) instead of printing the same misleading empty line forever.
            self.observe_fail_streak += 1
            self._set_health("stale" if self.observe_fail_streak < FAILURES_BEFORE_DEAD else "dead",
                             f"{self.observe_fail_streak} observe timeouts")
            backoff = min(MAX_OBSERVE_BACKOFF, IDLE_TURN_SECONDS * (1 << min(self.observe_fail_streak - 1, 4)))
            self.print(self.name, "warn",
                       f"observe timed out after {OBSERVE_TIMEOUT}s: the game isn't answering"
                       + (f" ({self.observe_fail_streak} in a row, retrying in {backoff:.0f}s)" if backoff else ""))
            if self.observe_fail_streak >= FAILURES_BEFORE_DEAD:
                self.dead = True
                self.print(self.name, "warn",
                           "MARKED DEAD: no game response; the watchdog will probe this instance, "
                           "reconnect when it answers again, and kill it if it keeps hanging.")
            else:
                await asyncio.sleep(backoff)
            return
        self.observe_fail_streak = 0
        self._set_health("ok")
        self.others = [o["name"] for o in obs.get("others", [])]
        me, world = obs["me"], obs["world"]
        self._dash(where=f"{me['location']} - energy {me['energy']}/{me['max_energy']}")
        if self.dashboard:
            self.dashboard.set_world(f"{world['date']}, {world['time']}, {world['weather']} - {me['money']}g")

        has_screen = self.screen_message is not None and time.monotonic() - self.screen_message_at < 30
        may_speak = self.addressed or self.report_turn or has_screen or time.monotonic() - self.last_say > MIN_SAY_GAP_SECONDS
        self.addressed = False
        self.report_turn = False
        events = "\n".join(f"- {e}" for e in self.inbox)
        self.inbox.clear()
        busy = bool(self.busy or self.queue)
        running = self.busy  # the job running when the prompt was made
        # No "Working on" note: the model is only asked when its plan is done or failed (or someone
        # addressed it mid-job), so the events and the observation's "Currently: ..." carry that context.
        status = "" if may_speak else "No 'say' action this turn."
        observation = prompts.format_observation(
            obs, events, status, self.screen_message if has_screen else None
        )
        prompt = f"{observation}\n\nWhat do you do?"

        t0 = time.monotonic()
        self.print(self.name, "prompt", prompt)
        try:
            raw = await self._ask(prompt)
            self.print(self.name, "reply", raw)
            decision = json.loads(raw)
        except Exception as e:  # noqa: BLE001
            self.print(self.name, "info", f"LLM error: {e}")
            return

        thought = decision.get("thought", "").strip()
        self.print(self.name, "thought", f"{thought}  ({time.monotonic() - t0:.1f}s)")
        # "say" and "emote" are actions, but they never queue: handled right away (say goes to the
        # game's chat, emote above the head).
        raw_actions = [a for a in decision.get("actions", []) if isinstance(a, dict)]
        # "answer" is immediate too: it pokes the current dialogue box instead of queueing a job.
        answer = next((a for a in raw_actions if a.get("do") == "answer"), None)
        if answer is not None:
            option = int(answer.get("n") or 0)
            self.print(self.name, "info", f"answer({option})")
            await self._send("answer", count=option)
            self.screen_message = None
        info_actions = [a for a in raw_actions if a.get("do") in INFO_ACTIONS]
        for info in info_actions:
            await self._query(info.get("do", ""), target=_blank_if_none(info.get("target")))
        say = " ".join(str(a.get("text", "")) for a in raw_actions if a.get("do") == "say").strip()
        if len(say) >= MAX_SAY_LENGTH:  # hit the limit mid-sentence
            say = say[: say.rfind(" ")].rstrip(",;:") + "…" if " " in say else say
        if say and may_speak:
            self.last_say = time.monotonic()
            self.print(self.name, "say", say)
            # Through the game's chat box, so the other farmers (and any spectators) see it; it comes
            # back to the others as a normal chat event.
            await self._send("say", text=say)
        if emote := next((a.get("emote") for a in raw_actions if a.get("do") == "emote"), None):
            await self._send("emote", emote=emote)

        # A real dialogue or menu is blocking the game. If the model ignored it and tried to walk
        # off, nudge it once to answer/react before running the plan. (Talking doesn't handle a
        # menu, so "say" alone still gets nudged.)
        kind = self.screen_message.get("kind") if has_screen and self.screen_message is not None else None
        if kind == "menu" and not answer and not self.screen_message_ignored:
            self.screen_message_ignored = True
            self.inbox.append("A menu is blocking you. React first: take/store its items, or answer to close it.")
            self.wake.set()
            return
        if kind in ("dialogue", "event") and not answer and not say and not self.screen_message_ignored:
            self.screen_message_ignored = True
            self.inbox.append("A message is still on screen. React to it first: answer n (or just answer to continue), or say something.")
            self.wake.set()
            return

        actions, errors = self._parse_actions([a for a in raw_actions if a.get("do") not in ("say", "emote", "answer") and a.get("do") not in INFO_ACTIONS], obs)
        self.inbox.extend(errors)
        if actions and actions[0].cmd == "stop":
            await self._send("stop")
            self.queue.clear()
            self.busy, self.busy_id = None, None
            actions = actions[1:]
        if actions:
            if any(a.cmd == "sleep" for a in actions):  # nothing after going to bed makes sense
                actions = actions[: next(i for i, a in enumerate(actions) if a.cmd == "sleep") + 1]
            self.print(self.name, "plan", " -> ".join(a.describe() for a in actions))
            if running is not None and actions[0] == running:
                # Asked mid-job, models tend to repeat the running action rather than leave 'actions' empty.
                # That means "keep going" (restarting it would lose progress, or redo a finished walk).
                self.queue = actions[1:]
                if self.busy is None:  # it finished while we were thinking
                    await self._start_next()
            else:
                self.queue = actions
                # Starting a job cancels the running one in the game; that job's job_done is ignored (id mismatch).
                await self._start_next()

        self._dash(thought=thought, plan=self._plan_text(), doing=self.busy.describe() if self.busy else "")
        if (self.busy or self.queue) and not self.addressed:
            # Things that woke us while we were thinking (the hour, an old job's result) wait in the inbox
            # until the plan is done; otherwise the agent takes another turn right after starting a plan.
            # (14% of the turns were like that, each one delaying the other agents' turns on the model.)
            self.wake.clear()


    def _parse_actions(self, raw: list[dict[str, Any]], obs: dict[str, Any]) -> tuple[list[Action], list[str]]:
        actions: list[Action] = []
        errors: list[str] = []
        empty_tilled = obs.get("farm", {}).get("empty_tilled_soil", 0)
        world = obs.get("world", {})
        t = world.get("time_raw", 1200)
        shop_closed = world.get("date", "").startswith("Wed") or not 900 <= t < 1700
        me = obs.get("me", {})
        tired = me.get("energy", 100) < 0.2 * me.get("max_energy", 270)
        farmers = {n.lower() for n in self.others}
        carried = _carried_items(me.get("inventory") or [])

        def carries(thing: str) -> bool:
            return _canonical_item(thing) in carried
        # x,y in a decision are absolute map coordinates.
        map_info = obs.get("map") or {}
        map_w, map_h = map_info.get("width"), map_info.get("height")
        for item in raw:
            cmd = item.get("do", "")
            spec = ACTIONS.get(cmd)
            if spec is None:
                continue
            args: dict[str, Any] = {}
            target = _blank_if_none(item.get("target"))
            thing = _blank_if_none(item.get("item"))
            # Models sometimes put the count into the item: "Parsnip Seeds n:5", "Parsnip Seeds x5".
            if (m := re.fullmatch(r"(.*?)[\s,]+(?:n\s*[:=]\s*|x\s*)(\d+)", thing)) and not item.get("n"):
                thing, item = m.group(1), {**item, "n": int(m.group(2))}
            has_xy = isinstance(item.get("x"), int) and isinstance(item.get("y"), int)
            if has_xy and spec.xy and map_w is not None and map_h is not None:
                if not (0 <= item["x"] < map_w and 0 <= item["y"] < map_h):
                    errors.append(f"Skipped {cmd} at {item['x']},{item['y']}: that is outside this map "
                                  f"(it is {map_w}x{map_h}).")
                    continue
            missing = [f for f in spec.needs if not {"target": target, "item": thing, "xy": has_xy}[f]]
            if missing:
                errors.append(f"'{cmd}' needs {' and '.join('x,y' if m == 'xy' else m for m in missing)}, so it was skipped.")
                continue
            if spec.target and target:
                args[spec.target] = target
            if spec.item and thing:
                args[spec.item] = thing
            if spec.xy and has_xy:
                args["x"], args["y"] = item["x"], item["y"]
            elif spec.n and item.get("n"):
                args["count"] = item["n"]
            if cmd == "talk_to" and target.lower() in farmers:
                errors.append(f"Skipped talk_to {target}: that's a fellow farmer - use 'say' to talk to them. "
                              "talk_to is for villagers.")
                continue
            if cmd in ("plant", "donate", "ship") and thing and not carries(thing):
                # The model repeats doomed 'plant/donate what I don't have' turns for a long time;
                # catch it before the game does, without failing the rest of the plan.
                if cmd == "plant":
                    hint = f"you don't have {thing}. Take the seeds from a chest (take {thing}) or buy them first."
                elif cmd == "donate":
                    hint = f"you're not carrying {thing}. Collect/grow it (or take it from a chest) before donating."
                else:
                    hint = f"you're not carrying {thing} - nothing to ship."
                errors.append(f"Skipped {cmd} {thing}: {hint}")
                continue
            if cmd == "sleep" and t < 1800 and not tired:
                errors.append(f"Skipped sleep: it's only {world.get('time')} and you have energy left. "
                              "Keep working; go to bed in the evening.")
                continue
            if cmd == "buy" and me.get("money", 0) < 20:
                if not any("no money" in e for e in errors):
                    errors.append(f"Skipped buy: there's no money ({me.get('money', 0)}g). Money comes from shipping "
                                  "(crops, forage), paid out overnight - help with that instead.")
                continue
            if cmd == "buy" and shop_closed:
                if not any("shops are closed" in e for e in errors):
                    errors.append(f"Skipped buy: the shops are closed right now ({world.get('time')}). They're open 9am-5pm "
                                  "(Pierre's not on Wednesdays). Do something else until then.")
                continue
            if cmd == "till" and not has_xy and empty_tilled >= MAX_EMPTY_TILLED:
                if not any("enough tilled" in e for e in errors):
                    errors.append(f"Skipped till: there are already {empty_tilled} empty tilled tiles waiting for seeds. "
                                  "Plant (or get seeds) instead of tilling more.")
                continue
            actions.append(Action(cmd, args))
        if any(a.cmd == "stop" for a in actions[1:]):
            actions = [a for a in actions if a.cmd != "stop"]
        return actions, errors

    async def _query(self, cmd: str, **args: Any) -> None:
        """Run an information action now and put its answer in the next inbox."""
        try:
            data = await self.conn.request(cmd, **args)
            summary = str(data.get("summary") or "")
            self.inbox.append(f"{cmd}: {summary}")
            self.print(self.name, "info", f"{cmd}: {summary}")
        except GameError as e:
            self.inbox.append(f"{cmd} failed: {e}")
        except (asyncio.TimeoutError, ConnectionError):
            self.print(self.name, "warn", f"{cmd}: no reply - game not responding")

    async def _ask(self, prompt: str) -> str:
        """ask().completed(), optionally timing tokens (NV_DEBUG_TIMING=1) and echoing them (NV_DEBUG_TOKENS=1)."""
        assert self.chat is not None
        # Shown as a spinner on the dashboard. The agents share one model, so a request may first wait
        # for the others' turns ("waiting") before its first token arrives ("thinking").
        self._dash(llm="waiting", llm_since=time.time())
        if DEBUG_TIMING:
            # KV-cache position before this ask: the retained history that has to be re-prefilled
            # if set_chat_history / reset has already invalidated the cache.
            ctx_before = (await self.chat.stats()).context_used
            prompt_tokens = len(await self.chat.tokenize(prompt))
        else:
            ctx_before, prompt_tokens = 0, 0
        try:
            t0 = time.monotonic()
            stream = self.chat.ask(prompt)
            parts: list[str] = []
            arrivals: list[float] = []  # monotonic time each token's text arrived
            while (token := await stream.next_token()) is not None:
                arrivals.append(time.monotonic())
                if not parts:
                    self._dash(llm="thinking")
                parts.append(token)
                if DEBUG_TOKENS:
                    print(f"[{self.name}] {token!r}", file=sys.stderr, flush=True)
            if DEBUG_TIMING:
                self._report_timing(t0, arrivals, ctx_before, prompt_tokens, parts)
            return "".join(parts)
        finally:
            self._dash(llm=None, llm_since=None)

    def _report_timing(self, t0: float, arrivals: list[float], ctx_before: int,
                       prompt_tokens: int, parts: list[str]) -> None:
        """
        Print a per-turn timing breakdown (used with NV_DEBUG_TIMING=1).

        The agents share one model, so a request first waits in queue behind the others' turns
        ("wait"), then the model re-reads the retained history and the new prompt ("prefill") and
        Only the combined first token is exposed, so:
        * tft (time to first token) = queue wait + prefill for the whole prompt.
        * ms/tok after the first = pure generation speed (decode).
        * ctx_before + prompt_tokens = the size of what had to be (re-)prefilled.
        """
        n = len(parts)
        if n == 0:
            out = (f"[t] {self.name} ask: NO TOKENS (total {time.monotonic() - t0:.2f}s)"
                   f" ctx_before={ctx_before} prompt={prompt_tokens}")
            print(out, file=sys.stderr, flush=True)
            return
        tft = arrivals[0] - t0  # queue wait + prefill of the whole present context
        # decode time = tokens after the first; use per-token gaps for a faithful ms/tok.
        gaps = [arrivals[i] - arrivals[i - 1] for i in range(1, n)]
        if gaps:
            decode_ms = sum(gaps) / len(gaps) * 1000.0
        else:
            decode_ms = float("nan")
        total = arrivals[-1] - t0
        # 90th-percentile token gap (a run of fast tokens interrupted by a slow one → schema re-eval).
        slow = max(gaps) * 1000.0 if gaps else 0.0
        out = (
            f"[t] {self.name} ask: {n} tokens total {total:.2f}s | "
            f"first-token {tft:.2f}s (wait+prefill) | "
            f"decode {decode_ms:.0f} ms/tok ({1e3 / decode_ms:.1f} tok/s) | "
            f"slowest-gap {slow:.0f}ms | "
            f"ctx_before={ctx_before} prompt={prompt_tokens} (prefill {ctx_before + prompt_tokens})"
        )
        print(out, file=sys.stderr, flush=True)
        # Token-by-token deltas: useful to spot where the time actually goes.
        for i in range(1, n):
            print(f"[t] {self.name}   tok {i:>3} +{gaps[i - 1] * 1000:6.0f}ms {parts[i]!r}",
                  file=sys.stderr, flush=True)

    def _dash(self, **fields: Any) -> None:
        if self.dashboard:
            self.dashboard.update(self.name, **fields)

    def _plan_text(self) -> str:
        return " -> ".join(a.describe() for a in ([self.busy] if self.busy else []) + self.queue)

    async def _start_next(self) -> None:
        if not self.queue:
            return
        action = self.queue.pop(0)
        self.busy, self.busy_id = action, None
        try:
            reply = await self.conn.request(ACTIONS[action.cmd].cmd, **action.args)
            self.busy_id = reply.get("job_id")
        except GameError as e:
            self.busy = None
            self.queue.clear()
            self.inbox.append(f"Couldn't {action.describe()}: {e}")
            self.wake.set()
        except (asyncio.TimeoutError, ConnectionError):
            # The game didn't answer at all - unresponsive. Count it like an observe failure and
            # stop taking turns once it's clearly hopeless, instead of crashing the agent task.
            self.busy = None
            self.queue.clear()
            self.observe_fail_streak += 1
            self.dead = self.dead or self.observe_fail_streak >= FAILURES_BEFORE_DEAD
            self._set_health("dead" if self.dead else "stale", f"no reply {self.observe_fail_streak}")
            self.print(self.name, "warn",
                       f"couldn't start {action.describe()}: no reply - the game isn't responding")
            if self.dead:
                self.print(self.name, "warn",
                           "MARKED DEAD: no game response; the watchdog will probe this instance.")
            self.wake.set()

    async def _send(self, cmd: str, **args: Any) -> None:
        """Fire-and-forget command (say, emote, stop)."""
        try:
            await self.conn.request(cmd, **args)
        except GameError as e:
            self.inbox.append(f"Couldn't {cmd}: {e}")
        except (asyncio.TimeoutError, ConnectionError):
            self.print(self.name, "warn", f"couldn't {cmd}: no reply - game not responding")
