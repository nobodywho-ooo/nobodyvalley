"""Run one LLM agent per connected Stardew Valley instance, all sharing a single NobodyWho model."""

from __future__ import annotations

import argparse
import asyncio
import logging
import sys
import time
import tomllib
from pathlib import Path
from typing import Any

from nobodywho import Model

from .agent import Agent
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


def make_printer(log_file: Path | None, prompt_dir: Path | None = None):
    colors: dict[str, str] = {}
    # A new nobodyvalley process starts a fresh log, rather than appending to a previous run
    # (the demo also gives each run its own folder).
    fh = log_file.open("w") if log_file else None
    prompt_files: dict[str, Any] = {}

    def printer(name: str, kind: str, text: str) -> None:
        stamp = time.strftime("%H:%M:%S")
        if kind in ("system", "history", "prompt", "reply"):
            # Full system prompts, persistent history, temporary prompts and replies: too long for
            # the terminal, but needed for debugging. Keep one file per farmer.
            if prompt_dir:
                prompt_dir.mkdir(parents=True, exist_ok=True)
                fh_prompt = prompt_files.get(name)
                if fh_prompt is None:
                    # "w" clears any prompts from an earlier nobodyvalley process for this farmer.
                    fh_prompt = prompt_files[name] = (prompt_dir / f"{name}.log").open("w")
                label = {
                    "system": "system prompt (persistent)",
                    "history": "chat history (persistent)",
                    "prompt": "current prompt (temporary; not kept in history)",
                    "reply": "reply (kept in history)",
                }[kind]
                fh_prompt.write(f"===== {stamp} {name} {label}\n{text}\n")
                fh_prompt.flush()
            return
        color = colors.setdefault(name, COLORS[len(colors) % len(COLORS)])
        print(f"{stamp} {color}{name:>10}{RESET} {kind:>7} {KIND_STYLE.get(kind, '')}{text}{RESET}", flush=True)
        if fh:
            fh.write(f"{stamp}\t{name}\t{kind}\t{text}\n")
            fh.flush()

    return printer


def default_personas_path() -> Path:
    """personas.local.toml (your own farmers, gitignored) if it exists, else personas.toml."""
    root = Path(__file__).parent.parent
    local = root / "personas.local.toml"
    return local if local.exists() else root / "personas.toml"


def load_personas(path: Path) -> dict[str, str]:
    """Farmer name (lowercase) -> "personality | job". Entries are tables, or "personality | job" strings."""
    if not path.exists():
        return {}
    data = tomllib.loads(path.read_text())
    personas = {}
    for name, value in data.get("personas", {}).items():
        if isinstance(value, dict):
            job = value.get("job", "").strip() or "help wherever you're needed"
            personas[name.lower()] = f"{value.get('personality', '').strip()} | {job}"
        else:
            personas[name.lower()] = value
    return personas




async def amain(args: argparse.Namespace) -> None:
    printer = make_printer(args.log, args.prompt_dir)

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

    personas_path = args.personas or default_personas_path()
    personas = load_personas(personas_path)
    running: dict[int, asyncio.Task[None]] = {}

    def persona_for(hello: dict) -> str:
        farmer = hello.get("farmer") or ""
        persona = personas.get(farmer.lower())
        if persona is None:
            sys.exit(f"No persona for farmer {farmer!r}: add a [personas.{farmer}] table to {personas_path} "
                     f"(or pass --personas), then restart.")
        return persona

    async def start_agent(conn: GameConnection) -> None:
        hello = await conn.request("hello")
        persona = persona_for(hello)
        index = conn.port - args.base_port
        agent = Agent(conn, model, persona, args.n_ctx, printer, start_delay=min(index, 4) * 4.0, dashboard=dashboard)
        running[conn.port] = asyncio.create_task(agent.run())

    for conn in conns:
        await start_agent(conn)

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
    p.add_argument("--personas", type=Path, default=None, help="personas file (default: personas.local.toml if it exists, else personas.toml)")
    p.add_argument("--log", type=Path, default=Path("agents.log"), help="also append everything to this file")
    p.add_argument("--prompt-dir", type=Path, default=Path("prompts"), help="write each farmer's full system prompt, turns and replies to <dir>/<Name>.log")
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
