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
from .bridge import discover
from .dashboard import Dashboard

DEFAULT_MODEL = "huggingface:unsloth/Qwen3.5-9B-GGUF/Qwen3.5-9B-Q4_K_M.gguf"

COLORS = ["\033[36m", "\033[35m", "\033[33m", "\033[32m", "\033[34m", "\033[31m"]
KIND_STYLE = {
    "thought": "\033[2m",  # dim
    "say": "\033[1m",  # bold
    "plan": "",
    "result": "",
    "diary": "\033[3m",  # italic
    "info": "\033[2m",
}
RESET = "\033[0m"


def make_printer(log_file: Path | None):
    colors: dict[str, str] = {}
    fh = log_file.open("a") if log_file else None

    def printer(name: str, kind: str, text: str) -> None:
        color = colors.setdefault(name, COLORS[len(colors) % len(COLORS)])
        stamp = time.strftime("%H:%M:%S")
        print(f"{stamp} {color}{name:>10}{RESET} {kind:>7} {KIND_STYLE.get(kind, '')}{text}{RESET}", flush=True)
        if fh:
            fh.write(f"{stamp}\t{name}\t{kind}\t{text}\n")
            fh.flush()

    return printer


def load_personas(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    data = tomllib.loads(path.read_text())
    return {k.lower(): v for k, v in data.get("personas", {}).items()}


async def amain(args: argparse.Namespace) -> None:
    printer = make_printer(args.log)

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
    agents: list[Agent] = []
    for i, conn in enumerate(conns):
        hello = await conn.request("hello")
        name = (hello.get("farmer") or "").lower()
        persona = personas.get(name) or prompts.DEFAULT_PERSONAS[i % len(prompts.DEFAULT_PERSONAS)]
        agents.append(Agent(conn, model, persona, args.n_ctx, printer, team=team, start_delay=i * 4.0, dashboard=dashboard))

    await asyncio.gather(*(a.run() for a in agents))


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default=DEFAULT_MODEL, help="GGUF path or huggingface: reference")
    p.add_argument("--n-ctx", type=int, default=8192, help="context size per agent")
    p.add_argument("--base-port", type=int, default=8765)
    p.add_argument("--max-instances", type=int, default=8)
    p.add_argument("--personas", type=Path, default=Path(__file__).parent.parent / "personas.toml")
    p.add_argument("--log", type=Path, default=Path("agents.log"), help="also append everything to this file")
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
