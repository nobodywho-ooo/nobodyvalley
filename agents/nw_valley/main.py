"""Run one LLM agent per connected Stardew Valley instance, all sharing a single NobodyWho model."""

from __future__ import annotations

import argparse
import asyncio
import logging
import sys
import time
import tomllib
from pathlib import Path

from nobodywho import Model

from . import prompts
from .agent import Agent, Team
from .bridge import GameConnection, discover
from .dashboard import Dashboard

DEFAULT_MODEL = "huggingface:unsloth/Qwen3.5-9B-GGUF/Qwen3.5-9B-Q4_K_M.gguf"

COLORS = ["\033[36m", "\033[35m", "\033[33m", "\033[32m", "\033[34m", "\033[31m"]
KIND_STYLE = {
    "thought": "\033[2m",  # dim
    "say": "\033[1m",  # bold
    "plan": "",
    "result": "",
    "info": "\033[2m",
}
RESET = "\033[0m"


def make_printer(log_file: Path | None, prompt_log: Path | None = None):
    colors: dict[str, str] = {}
    fh = log_file.open("a") if log_file else None
    ph = prompt_log.open("a") if prompt_log else None

    def printer(name: str, kind: str, text: str) -> None:
        stamp = time.strftime("%H:%M:%S")
        if kind in ("prompt", "reply"):
            # Full LLM prompts and replies: too long for the terminal, but needed for debugging.
            if ph:
                ph.write(f"===== {stamp} {name} {kind}\n{text}\n")
                ph.flush()
            return
        color = colors.setdefault(name, COLORS[len(colors) % len(COLORS)])
        print(f"{stamp} {color}{name:>10}{RESET} {kind:>7} {KIND_STYLE.get(kind, '')}{text}{RESET}", flush=True)
        if fh:
            fh.write(f"{stamp}\t{name}\t{kind}\t{text}\n")
            fh.flush()

    return printer


def load_personas(path: Path) -> dict[str, tuple[str, str]]:
    """Farmer name (lowercase) -> (personality, job). Entries are tables, or "personality | job" strings."""
    if not path.exists():
        return {}
    data = tomllib.loads(path.read_text())
    personas = {}
    for name, value in data.get("personas", {}).items():
        if isinstance(value, dict):
            personas[name.lower()] = (value.get("personality", "").strip(),
                                      value.get("job", "").strip() or "help wherever you're needed")
        else:
            personas[name.lower()] = prompts.persona_parts(value)
    return personas


async def amain(args: argparse.Namespace) -> None:
    printer = make_printer(args.log, args.prompt_log)

    conns = await discover(args.base_port, args.max_instances)
    if not conns:
        sys.exit(f"No game instances found on ports {args.base_port}-{args.base_port + args.max_instances - 1}. "
                 "Is the NobodyValley mod installed and the game running?")
    printer("main", "info", f"found {len(conns)} game instance(s) on ports {', '.join(str(c.port) for c in conns)}")

    printer("main", "info", f"loading model {args.model} ...")
    model = await Model.load_model_async(args.model) if hasattr(Model, "load_model_async") else Model(args.model)
    printer("main", "info", "model loaded")

    dashboard = None
    if not args.no_dashboard:
        dashboard = Dashboard(args.dashboard_port)
        dashboard.start()
        printer("main", "info", f"dashboard at {dashboard.url}")
        dashboard.open_window(args.dashboard_window)

    personas = load_personas(args.personas)
    team = Team()
    running: dict[int, asyncio.Task[None]] = {}

    def persona_for(conn: GameConnection, hello: dict) -> tuple[str, str]:
        # Instances bind ports in launch order (host first), so the port picks a stable default role.
        index = conn.port - args.base_port
        return personas.get((hello.get("farmer") or "").lower()) or prompts.DEFAULT_PERSONAS[index % len(prompts.DEFAULT_PERSONAS)]

    async def start_agent(conn: GameConnection, hello: dict | None = None) -> None:
        hello = hello or await conn.request("hello")
        persona = persona_for(conn, hello)
        if hello.get("farmer"):
            team.roles[hello["farmer"]] = persona[1]
        index = conn.port - args.base_port
        agent = Agent(conn, model, persona, args.n_ctx, printer, team=team, start_delay=min(index, 4) * 4.0, dashboard=dashboard)
        running[conn.port] = asyncio.create_task(agent.run())

    # Everyone's role first, so each agent's prompt can say who does what.
    hellos = [await conn.request("hello") for conn in conns]
    for conn, hello in zip(conns, hellos):
        if hello.get("farmer"):
            team.roles[hello["farmer"]] = persona_for(conn, hello)[1]
    for conn, hello in zip(conns, hellos):
        await start_agent(conn, hello)

    # Keep an eye out for instances that join (or come back) later.
    while True:
        await asyncio.sleep(10)
        for port, task in list(running.items()):
            if task.done():
                if not task.cancelled() and task.exception():
                    printer("main", "info", f"agent on port {port} stopped: {task.exception()!r}")
                del running[port]
        for conn in await discover(args.base_port, args.max_instances, skip=set(running)):
            printer("main", "info", f"new game instance on port {conn.port}")
            await start_agent(conn)


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default=DEFAULT_MODEL, help="GGUF path or huggingface: reference")
    p.add_argument("--n-ctx", type=int, default=32768, help="context size per agent (tokens)")
    p.add_argument("--base-port", type=int, default=8765)
    p.add_argument("--max-instances", type=int, default=8)
    p.add_argument("--personas", type=Path, default=Path(__file__).parent.parent / "personas.toml")
    p.add_argument("--log", type=Path, default=Path("agents.log"), help="also append everything to this file")
    p.add_argument("--prompt-log", type=Path, default=Path("prompts.log"), help="append every LLM prompt and reply to this file")
    p.add_argument("--dashboard-port", type=int, default=8780)
    p.add_argument("--dashboard-window", metavar="X,Y,W,H", help="where to put the dashboard window (screen points)")
    p.add_argument("--no-dashboard", action="store_true", help="don't serve/open the dashboard")
    p.add_argument("-v", "--verbose", action="store_true")
    args = p.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.WARNING)
    try:
        asyncio.run(amain(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
