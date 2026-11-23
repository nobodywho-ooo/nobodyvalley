"""Run one LLM agent per connected Stardew Valley instance, each on its own model.

A farmer's model comes from the `model` key in their persona (see personas.toml), falling back
to --model / NV_MODEL. Local models are loaded once per *distinct* model (one NobodyWho Model
instance, shared by all farmers on that model — the sizes of the different models in use add up
in RAM). qwen3.8-flash-next has no local weights: the agents share one client to a vLLM server.
"""

from __future__ import annotations

import argparse
import asyncio
import logging
import os
import subprocess
import sys
import time
import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .agent import DECISION_SCHEMA, Agent
from .backend import Backend, LocalBackend, RemoteBackend
from .bridge import GameConnection, GameError, discover
from .dashboard import Dashboard

DEFAULT_MODEL = "huggingface:unsloth/Qwen3.5-9B-GGUF/Qwen3.5-9B-Q4_K_M.gguf"

# Named choices for --model / NV_MODEL, and what each means.
MODEL_PRESETS = {
    "gemma4-e2b": "huggingface:unsloth/gemma-4-E2B-it-GGUF/gemma-4-E2B-it-Q4_K_M.gguf",
    "gemma4-12b": "huggingface:unsloth/gemma-4-12b-it-GGUF/gemma-4-12b-it-Q4_K_M.gguf",
    "qwen3.5-9b": DEFAULT_MODEL,
}
# Served over the OpenAI-compatible vLLM API; the agents don't load any local model for it.
REMOTE_MODEL = "qwen3.8-flash-next"



def resolve_model(model_arg: str) -> tuple[str, str | None]:
    """(kind, ref): kind is 'local' (a GGUF on this Mac) or 'remote' (a vLLM server)."""
    if model_arg == REMOTE_MODEL:
        return "remote", None
    return "local", MODEL_PRESETS.get(model_arg, model_arg)

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
    # Append rather than truncate: the run's folder is per-run and owns these files, and a python
    # process restart (e.g. a code fix mid-run) shouldn't throw away what a farmer already said.
    fh = log_file.open("a") if log_file else None
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
                    # Append: a restart of this process keeps the farmer's earlier prompts.
                    fh_prompt = prompt_files[name] = (prompt_dir / f"{name}.log").open("a")
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


@dataclass
class Persona:
    text: str  # "personality | job", shown to the model
    model: str | None = None  # a preset name / GGUF ref for THIS farmer; None = the --model default


def load_personas(path: Path) -> dict[str, Persona]:
    """Farmer name (lowercase) -> Persona. Entries are tables (personality, job, optional model),
    or the old "personality | job" string form (no model)."""
    if not path.exists():
        return {}
    data = tomllib.loads(path.read_text())
    personas = {}
    for name, value in data.get("personas", {}).items():
        if isinstance(value, dict):
            job = value.get("job", "").strip() or "help wherever you're needed"
            model = str(value.get("model") or "").strip() or None
            personas[name.lower()] = Persona(f"{value.get('personality', '').strip()} | {job}", model)
        else:
            personas[name.lower()] = Persona(value)
    return personas




async def amain(args: argparse.Namespace) -> None:
    printer = make_printer(args.log, args.prompt_dir)

    conns = await discover(args.base_port, args.max_instances)
    if not conns:
        sys.exit(f"No game instances found on ports {args.base_port}-{args.base_port + args.max_instances - 1}. "
                 "Is the NobodyValley mod installed and the game running?")
    printer("main", "info", f"found {len(conns)} game instance(s) on ports {', '.join(str(c.port) for c in conns)}")

    # One backend per *distinct* model: remote = the shared vLLM client; local = one
    # LocalBackend per ref, whose NobodyWho Model is loaded once and shared by all farmers on it.
    remote_backend: RemoteBackend | None = None
    local_backends: dict[str, LocalBackend] = {}

    async def resolve_backend(model_arg: str, who: str) -> Backend:
        """The backend for one farmer: remote = the shared vLLM client; local = the cached
        backend for that ref (a new model is only loaded when the ref hasn't been seen yet)."""
        if (model_arg not in MODEL_PRESETS and model_arg != REMOTE_MODEL
                and "/" not in model_arg and not model_arg.lower().endswith(".gguf")):
            sys.exit(f"unknown model {model_arg!r} for {who}: choose from "
                     "gemma4-e2b, gemma4-12b, qwen3.5-9b, qwen3.8-flash-next, "
                     "or a GGUF path / huggingface: reference (see personas.toml / --model)")
        kind, ref = resolve_model(model_arg)
        if kind != "remote":
            backend = local_backends.get(ref)
            if backend is None:
                backend = local_backends[ref] = LocalBackend(ref, DECISION_SCHEMA)
            return backend
        nonlocal remote_backend
        if remote_backend is None:
            url = os.environ.get("NV_VLLM_URL", "")
            api_key = os.environ.get("NV_VLLM_API_KEY", "")
            if not url or not api_key:
                sys.exit("qwen3.8-flash-next is served over vLLM and needs the endpoint and key: "
                         "set NV_VLLM_URL and NV_VLLM_API_KEY in scripts/local.env.")
            printer("main", "info", f"connecting to Qwen3.8-Flash-Next over vLLM at {url}")
            remote_backend = RemoteBackend(url, api_key, DECISION_SCHEMA)
            try:
                await remote_backend.check()
            except Exception as e:
                sys.exit(f"Can't reach vLLM at {url}: {e}. Check NV_VLLM_URL and NV_VLLM_API_KEY "
                         "(scripts/local.env).")
            printer("main", "info", "remote model ready")
        return remote_backend

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

    def persona_for(hello: dict) -> Persona:
        farmer = hello.get("farmer") or ""
        persona = personas.get(farmer.lower())
        if persona is None:
            sys.exit(f"No persona for farmer {farmer!r}: add a [personas.{farmer}] table to {personas_path} "
                     f"(or pass --personas), then restart.")
        return persona

    async def start_agent(conn: GameConnection) -> None:
        hello = await conn.request("hello")
        persona = persona_for(hello)
        farmer = hello.get("farmer") or f"port {conn.port}"
        # Reserve the dashboard panel now, in discovery order, so the panels don't reorder
        # themselves as the farmhands connect at staggered times (host is discovered first).
        if dashboard:
            dashboard.reserve(farmer)
        # The farmer's own model if their persona sets one, else the --model / NV_MODEL default.
        backend = await resolve_backend(persona.model or args.model, farmer)
        index = conn.port - args.base_port
        agent = Agent(conn, backend, persona.text, args.n_ctx, printer, start_delay=min(index, 4) * 4.0,
                      dashboard=dashboard, model=persona.model or args.model)
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
    p.add_argument("--model", default=DEFAULT_MODEL,
                   help="default model for farmers without a `model` in their persona: one of "
                        "{gemma4-e2b, gemma4-12b, qwen3.5-9b, qwen3.8-flash-next}, "
                        "or a GGUF path / huggingface: reference")
    p.add_argument("--n-ctx", type=int, default=16384, help="context size per agent (tokens)")
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
    # A bare word that isn't a preset is usually a typo; a path or huggingface: ref is passed on.
    if (args.model not in MODEL_PRESETS and args.model != REMOTE_MODEL
            and "/" not in args.model and not args.model.lower().endswith(".gguf")):
        p.error(f"unknown model {args.model!r}; choose from "
                "gemma4-e2b, gemma4-12b, qwen3.5-9b, qwen3.8-flash-next")
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.WARNING)
    try:
        asyncio.run(amain(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
