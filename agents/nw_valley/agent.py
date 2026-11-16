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
    "clear": Spec("clear_debris", n=True, xy=True),
    "chop": Spec("chop_trees", n=True, xy=True),
    "mine": Spec("mine", n=True, xy=True),
    "till": Spec("till", n=True, xy=True),
    "plant": Spec("plant", item="seed", n=True, xy=True),
    "water": Spec("water", n=True, xy=True),
    "harvest": Spec("harvest", n=True, xy=True),
    "ship": Spec("ship", item="item"),
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
# Don't till more when this much tilled soil is already waiting for seeds.
MAX_EMPTY_TILLED = 30

MAX_ACTIONS_PER_TURN = 4

DECISION_SCHEMA = {
    "type": "object",
    # Compact JSON: no whitespace, and fields that aren't needed are simply left out (fewer tokens).
    "x-guidance": {"whitespace_flexible": False},
    "properties": {
        "thought": {"type": "string", "maxLength": 160},
        "say": {"type": "string", "maxLength": 100},
        "emote": {"enum": EMOTES},
        "actions": {
            "type": "array",
            "maxItems": MAX_ACTIONS_PER_TURN,
            "items": {
                "type": "object",
                "properties": {
                    "do": {"enum": list(ACTIONS)},
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
# Keep the chat calm: an agent speaks at most this often (unless answering someone who addressed it).
MIN_SAY_GAP_SECONDS = 90.0
DEBUG_TOKENS = bool(os.environ.get("NV_DEBUG_TOKENS"))


@dataclass
class Action:
    cmd: str
    args: dict[str, Any] = field(default_factory=dict)

    def describe(self) -> str:
        a = ", ".join(f"{v}" for k, v in self.args.items() if k not in ("x", "y"))
        if "x" in self.args:
            a = f"{self.args['x']},{self.args['y']}" + (f", {a}" if a else "")
        return f"{self.cmd}({a})" if a else self.cmd


class Team:
    """Shared between all agents in this process: their plans, and the chat between them.

    The agents' chat stays out of the game (it would pop up in every window); it goes from agent to
    agent here, and is shown in the dashboard.
    """

    def __init__(self) -> None:
        self.plans: dict[str, str] = {}
        self.members: dict[str, "Agent"] = {}

    def others(self, me: str) -> str:
        return "; ".join(f"{name}: {plan}" for name, plan in self.plans.items() if name != me and plan)

    def say(self, speaker: str, text: str) -> None:
        for name, agent in self.members.items():
            if name != speaker:
                agent.hear(speaker, text)


class Agent:
    def __init__(self, conn: GameConnection, model: Model, persona: str | None, n_ctx: int, printer: Callable[[str, str, str], None],
                 team: Team | None = None, start_delay: float = 0.0, dashboard: Dashboard | None = None):
        self.dashboard = dashboard
        self.conn = conn
        self.model = model
        self.persona_override = persona
        self.n_ctx = n_ctx
        self.print = printer  # (agent name, kind, text)
        self.team = team or Team()
        self.start_delay = start_delay

        self.name = f"port{conn.port}"
        self.others: list[str] = []
        self.chat: ChatAsync | None = None

        self.queue: list[Action] = []  # actions waiting to be started
        self.busy: Action | None = None  # job currently running in the game
        self.busy_id: int | None = None  # the mod's id for that job
        self.asleep = False  # in bed; no turns until the next morning
        self.inbox: list[str] = []  # things that happened since the last turn
        self.wake = asyncio.Event()
        self.last_turn = 0.0
        self.last_failure: str | None = None
        self.last_say = 0.0
        self.idle_turns = 0  # consecutive turns where an idle agent chose to do nothing
        self.addressed = False  # someone spoke to us by name since our last turn

    # ------------------------------------------------------------------ lifecycle

    async def run(self) -> None:
        hello = await self.conn.request("hello")
        while not hello.get("world_ready"):
            self.print(self.name, "info", "waiting for a save to be loaded...")
            await self._wait_for_event("world_ready")
            hello = await self.conn.request("hello")
        self.name = hello["farmer"]
        self.team.members[self.name] = self

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
        self.inbox.append("You just woke up and are ready to start.")

        events = asyncio.create_task(self._event_loop())
        # Stagger the first turns, so later agents see what the earlier ones picked.
        await asyncio.sleep(self.start_delay)
        self.wake.set()
        try:
            await self._turn_loop()
        finally:
            events.cancel()

    def _persona(self) -> str | tuple[str, str]:
        return self.persona_override or prompts.DEFAULT_PERSONAS[0]

    def _system_prompt(self) -> str:
        return prompts.system_prompt(self.name, self.others, self._persona(), EMOTES)

    def hear(self, speaker: str, text: str, private: bool = False) -> None:
        """A chat message from another farmer (another agent, or a human typing in the game)."""
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
                # Typed into the game's chat (e.g. by a human player); agents talk via the Team.
                if ev["from"] not in self.team.members:
                    self.hear(ev["from"], ev["text"], bool(ev.get("private")))
            elif kind == "day_started":
                await self._new_day(ev.get("date", "a new day"))
            elif kind == "time":
                self._on_time(ev)

    def _on_time(self, ev: dict[str, Any]) -> None:
        t = ev.get("time_raw", 0)
        if self.asleep or (self.busy is not None and self.busy.cmd == "sleep"):
            return
        if t >= 2500:
            # Past 1am: don't leave it to the model, or everyone passes out at 2am.
            self.print(self.name, "info", "it's very late; sending them to bed")
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
        self._publish_plan()
        self.print(self.name, "result", f"{'Done' if ev['ok'] else 'Failed'}: {ev['summary']}")
        self._dash(result=ev["summary"], result_ok=ev["ok"], doing=self.busy.describe() if self.busy else "", plan=self._plan_text())

        if ev["ok"] and ev["action"] == "sleep":
            self.asleep = True
            self.queue.clear()
            return
        if ev["ok"]:
            self.last_failure = None
            if self.queue:
                asyncio.create_task(self._start_next())
                return
            self.inbox.append(f"Done {ev['action']}: {ev['summary']}")
        else:
            failure = f"{ev['action']}: {ev['summary']}"
            note = " (This failed the same way last time - do something different!)" if failure == self.last_failure else ""
            if "inventory is full" in ev["summary"]:
                note += (" Make room: ship things you don't need (e.g. ship Stone, Clay or Fiber; keep Wood, Coal and "
                         "seeds), or ship your forage.")
            self.last_failure = failure
            if self.queue:
                note += f" (Cancelled the rest of your plan: {', '.join(a.cmd for a in self.queue)}.)"
                self.queue.clear()
            self.inbox.append(f"Failed {failure}{note}")
        self.wake.set()

    async def _new_day(self, date: str) -> None:
        if self.chat is None:
            return
        # Start each day with a fresh context (the observation carries everything that matters).
        await self.chat.reset(self._system_prompt(), [])
        self.queue.clear()
        self.busy, self.busy_id = None, None
        self.asleep = False
        self.inbox = [f"Good morning! It's {date}. Plan your day."]
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
            obs = await self.conn.request("observe")
        except (GameError, asyncio.TimeoutError) as e:
            self.print(self.name, "info", f"observe failed: {e}")
            return
        self.others = [o["name"] for o in obs.get("others", [])]
        me, world = obs["me"], obs["world"]
        self._dash(where=f"{me['location']} - energy {me['energy']}/{me['max_energy']}")
        if self.dashboard:
            self.dashboard.set_world(f"{world['date']}, {world['time']}, {world['weather']} - {me['money']}g")

        may_speak = self.addressed or time.monotonic() - self.last_say > MIN_SAY_GAP_SECONDS
        self.addressed = False
        events = "\n".join(f"- {e}" for e in self.inbox) or "- nothing new"
        self.inbox.clear()
        busy = bool(self.busy or self.queue)
        plan_note = ""
        if busy:
            plan_note = (f"\nYou're busy with your plan: {self._plan_text()}. Keep going: leave 'actions' empty "
                         "(you can still say something), unless you really need to change plans.")
        team_note = f"\nWhat the others plan: {t}." if (t := self.team.others(self.name)) else ""
        speak_note = "" if may_speak else "\nYou spoke recently, so leave 'say' empty this turn."
        prompt = (f"What happened:\n{events}\n\n{prompts.format_observation(obs)}{team_note}{plan_note}{speak_note}\n\n"
                  f"Your job: {prompts.persona_parts(self._persona())[1]}. What do you do?")

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
        say = decision.get("say", "").strip()
        if len(say) >= DECISION_SCHEMA["properties"]["say"]["maxLength"]:  # hit the limit mid-sentence
            say = say[: say.rfind(" ")].rstrip(",;:") + "…" if " " in say else say
        if say and may_speak:
            self.last_say = time.monotonic()
            self.print(self.name, "say", say)
            if self.dashboard:
                self.dashboard.said(self.name, say)
            self.team.say(self.name, say)
        if emote := decision.get("emote"):
            await self._send("emote", emote=emote)

        actions, errors = self._parse_actions(decision.get("actions", []), obs)
        self.inbox.extend(errors)
        if actions and actions[0].cmd == "stop":
            await self._send("stop")
            self.queue.clear()
            self.busy, self.busy_id = None, None
            actions = actions[1:]
        if actions:
            if any(a.cmd == "sleep" for a in actions):  # nothing after going to bed makes sense
                actions = actions[: next(i for i, a in enumerate(actions) if a.cmd == "sleep") + 1]
            self.queue = actions
            self.print(self.name, "plan", " -> ".join(a.describe() for a in actions))
            # Starting a job cancels the running one in the game; that job's job_done is ignored (id mismatch).
            await self._start_next()
        elif not busy and not self.queue:
            self.idle_turns += 1
            # Nudge once right away; after that, wait for the idle timer instead of spinning.
            self.inbox.append("You didn't choose any actions, so you're standing around. "
                              "If there's nothing to do, forage for something to ship, chop trees, or visit a villager.")
            if self.idle_turns == 1:
                self.wake.set()
        if actions:
            self.idle_turns = 0

        self._publish_plan()
        self._dash(thought=thought, plan=self._plan_text(), doing=self.busy.describe() if self.busy else "")

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
        for item in raw:
            cmd = item.get("do", "")
            spec = ACTIONS.get(cmd)
            if spec is None:
                continue
            args: dict[str, Any] = {}
            target = (item.get("target") or "").strip()
            thing = (item.get("item") or "").strip()
            has_xy = isinstance(item.get("x"), int) and isinstance(item.get("y"), int)
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
            # Merge bulk repeats like till(4) -> till(4), or water -> water.
            prev = actions[-1] if actions else None
            if prev and prev.cmd == cmd and not has_xy and "x" not in prev.args and \
                    {k: v for k, v in prev.args.items() if k != "count"} == {k: v for k, v in args.items() if k != "count"}:
                if spec.n:
                    if "count" in prev.args and "count" in args:
                        prev.args["count"] = min(25, prev.args["count"] + args["count"])
                    else:
                        prev.args.pop("count", None)  # one of them meant "all"
                continue
            actions.append(Action(cmd, args))
        if any(a.cmd == "stop" for a in actions[1:]):
            actions = [a for a in actions if a.cmd != "stop"]
        return actions, errors

    async def _ask(self, prompt: str) -> str:
        """ask().completed(), optionally echoing tokens (NV_DEBUG_TOKENS=1)."""
        assert self.chat is not None
        # Shown as a spinner on the dashboard. The agents share one model, so a request may first wait
        # for the others' turns ("waiting") before its first token arrives ("thinking").
        self._dash(llm="waiting", llm_since=time.time())
        try:
            stream = self.chat.ask(prompt)
            parts: list[str] = []
            while (token := await stream.next_token()) is not None:
                if not parts:
                    self._dash(llm="thinking")
                parts.append(token)
                if DEBUG_TOKENS:
                    print(f"[{self.name}] {token!r}", file=sys.stderr, flush=True)
            return "".join(parts)
        finally:
            self._dash(llm=None, llm_since=None)

    def _dash(self, **fields: Any) -> None:
        if self.dashboard:
            self.dashboard.update(self.name, **fields)

    def _plan_text(self) -> str:
        return " -> ".join(a.describe() for a in ([self.busy] if self.busy else []) + self.queue)

    def _publish_plan(self) -> None:
        self.team.plans[self.name] = self._plan_text()

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

    async def _send(self, cmd: str, **args: Any) -> None:
        """Fire-and-forget command (say, emote, stop)."""
        try:
            await self.conn.request(cmd, **args)
        except GameError as e:
            self.inbox.append(f"Couldn't {cmd}: {e}")
