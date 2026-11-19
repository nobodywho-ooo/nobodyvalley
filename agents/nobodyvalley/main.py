"""Run one LLM agent per connected Stardew Valley instance, all sharing a single NobodyWho model."""

from __future__ import annotations

import argparse
import asyncio
import logging
import os
import subprocess
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
    "warn": "\033[1;31m",  # bold red: agent/game health problems
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
        if kind in ("system", "prompt", "reply"):
            # Full system prompts, prompts and replies: too long for the terminal, but needed for
            # debugging. Keep one file per farmer.
            if prompt_dir:
                prompt_dir.mkdir(parents=True, exist_ok=True)
                fh_prompt = prompt_files.get(name)
                if fh_prompt is None:
                    # "w" clears any prompts from an earlier nobodyvalley process for this farmer.
                    fh_prompt = prompt_files[name] = (prompt_dir / f"{name}.log").open("w")
                label = {
                    "system": "system prompt (persistent)",
                    "prompt": "current prompt (observation; kept in the chat history)",
                    "reply": "reply (kept in the chat history)",
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
    agents: dict[int, Agent] = {}

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
        agent.is_host = bool(hello.get("is_host"))
        agents[conn.port] = agent
        running[conn.port] = asyncio.create_task(agent.run())

    for conn in conns:
        await start_agent(conn)

    # ---------------------------------------------------------------- watchdog
    # A game instance that hard-freezes (or a socket we lose) would otherwise leave its agent
    # spinning on observe timeouts forever - and worse, a dead farmhand deadlocks the whole co-op
    # at the next day transition. So: agents stop taking turns once the game clearly isn't
    # answering (MARKED DEAD), and this monitor restarts them when the game answers again, and - if
    # enabled - kills a confirmed-hung farmhand's process (never the host) so the session goes on.
    STALE_AFTER = float(os.environ.get("NV_STALE_AFTER", "300"))  # no message at all for 5 min
    KILL_DEAD = os.environ.get("NV_WATCHDOG_KILL", "").lower() in ("1", "true", "yes")
    last_warn: dict[int, float] = {}
    killed: set[int] = set()

    async def probe_alive(port: int) -> bool:
        """Ask the mod for a hello. A bare TCP connect isn't enough: the kernel completes the
        handshake even when the game thread is hung, so only a reply proves it's alive."""
        conn = GameConnection(port)
        try:
            await conn.connect(timeout=2.0)
            await conn.request("hello", timeout=5.0)
            return True
        except (OSError, asyncio.TimeoutError, GameError):
            return False
        finally:
            await conn.close()

    def pids_on_port(port: int) -> list[str]:
        try:
            out = subprocess.run(["lsof", "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"],
                                 capture_output=True, text=True, timeout=5)
        except (OSError, subprocess.TimeoutExpired):
            return []
        return [p for p in out.stdout.split() if p.isdigit()]

    async def stop_agent(port: int, agent: Agent) -> None:
        task = running.pop(port, None)
        if task is not None and not task.done():
            task.cancel()
        await agent.conn.close()
        agents.pop(port, None)

    async def restart_agent(port: int) -> None:
        conn = GameConnection(port)
        try:
            await conn.connect(timeout=2.0)
            await start_agent(conn)  # re-adds it to running/agents
            last_warn.pop(port, None)
        except (OSError, asyncio.TimeoutError, GameError) as e:
            await conn.close()
            printer("main", "warn", f"port {port}: couldn't restart the agent: {e!r}")

    async def kill_instance(port: int) -> None:
        pids = await asyncio.to_thread(pids_on_port, port)
        if not pids:
            printer("main", "warn", f"port {port}: nothing is listening on it - nothing to kill")
            return
        printer("main", "warn", f"port {port}: killing the hung game instance (pid(s) {', '.join(pids)}) "
                                 "so the co-op can continue without it")
        await asyncio.to_thread(lambda: subprocess.run(["kill", "-TERM", *pids], check=False))
        await asyncio.sleep(3)
        if still := await asyncio.to_thread(pids_on_port, port):
            await asyncio.to_thread(lambda: subprocess.run(["kill", "-KILL", *still], check=False))

    async def monitor_loop() -> None:
        while True:
            try:
                await asyncio.sleep(10)
                now = time.monotonic()
                # Clean up ended agent tasks (crashed, or marked itself dead after repeated
                # no-answers). The per-port handling below then restores or kills the instance.
                for port, task in list(running.items()):
                    if not task.done():
                        continue
                    if not task.cancelled() and (exc := task.exception()):
                        printer("main", "info", f"agent on port {port} stopped: {exc!r}")
                    agent = agents.get(port)
                    if agent is not None:
                        await agent.conn.close()
                    del running[port]
                # Every known port, agent up or down: re-hire a down agent as soon as its game
                # answers again, and (opt-in) kill a farmhand whose game is genuinely hung - even
                # one whose agent already gave up - so the co-op keeps going instead of hanging at
                # the next day transition.
                for port, agent in list(agents.items()):
                    if port in killed:
                        continue
                    stale = now - agent.conn.last_live
                    hungry = stale >= STALE_AFTER
                    down = port not in running
                    if hungry:
                        if now - last_warn.get(port, 0) >= STALE_AFTER:
                            printer("main", "warn",
                                    f"port {port} ({agent.name}): no game response for {stale:.0f}s - "
                                    "instance looks hung")
                            last_warn[port] = now
                        if KILL_DEAD and not agent.is_host:
                            await kill_instance(port)
                            killed.add(port)
                            await stop_agent(port, agent)
                            continue
                        if await probe_alive(port):
                            # Game answers but our link is stale/broken: reconnect over a fresh one.
                            printer("main", "info", f"port {port}: answered a hello - reconnecting its agent")
                            await stop_agent(port, agent)
                            await restart_agent(port)
                        continue
                    if down:
                        # Agent is down (crashed / marked dead / just finished): bring it back the
                        # moment the game replies, without waiting for a staleness threshold.
                        if await probe_alive(port):
                            printer("main", "info", f"port {port}: game answers again, restarting its agent")
                            await restart_agent(port)
                        continue
                    last_warn.pop(port, None)
            except Exception as e:  # noqa: BLE001 - the watchdog itself must never die silently
                printer("main", "info", f"watchdog error: {e!r}")

    watchdog_task = asyncio.create_task(monitor_loop())

    # Keep an eye out for instances that join (or come back) later.
    while True:
        await asyncio.sleep(10)
        for conn in await discover(args.base_port, args.max_instances, skip=set(running) | set(agents)):
            printer("main", "info", f"new game instance on port {conn.port}")
            await start_agent(conn)


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", default=DEFAULT_MODEL, help="GGUF path or huggingface: reference")
    p.add_argument("--n-ctx", type=int, default=65536, help="context size per agent (tokens)")
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
