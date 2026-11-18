"""A small live web page showing what every agent is thinking and doing (one panel each).

Served from the agent process at http://127.0.0.1:8780 (stdlib only). The page polls /state every second.
"""

from __future__ import annotations

import json
import shutil
import socketserver
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

PAGE = """<!doctype html>
<html><head><meta charset="utf-8"><title>NobodyValley</title>
<style>
  body { margin: 0; padding: 10px; background: #1d1b26; color: #eee; font: 13px/1.35 -apple-system, sans-serif; }
  h1 { font-size: 14px; margin: 0 0 8px; color: #f6c86b; }
  /* Fixed-ish height, so the panels don't jump around as the text changes: room for the usual
     content, and the longer fields are cut off after a few lines (the full text is in a tooltip). */
  .agent { background: #2b2838; border-radius: 6px; padding: 8px 10px; margin-bottom: 8px; border-left: 4px solid var(--c);
           min-height: 13em; box-sizing: border-box; }
  .clamp2, .clamp3 { display: -webkit-box; -webkit-box-orient: vertical; overflow: hidden; }
  .clamp2 { -webkit-line-clamp: 2; }
  .clamp3 { -webkit-line-clamp: 3; }
  .name { font-weight: 600; color: var(--c); }
  .meta { color: #999; font-size: 11px; float: right; }
  .doing { margin-top: 3px; }
  .llm { color: #f6c86b; font-size: 11px; margin-left: 6px; }
  .llm.waiting { color: #999; }
  .spin { display: inline-block; width: 9px; height: 9px; margin-right: 4px; vertical-align: -1px;
          border: 2px solid currentColor; border-right-color: transparent; border-radius: 50%;
          animation: spin 0.8s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  .thought { margin-top: 3px; color: #cfcfe8; font-style: italic; }
  .plan { margin-top: 3px; color: #9fd3a8; font-size: 12px; }
  .result { margin-top: 3px; color: #aaa; font-size: 12px; }
  .result.failed { color: #e99; }
</style></head>
<body>
<h1 id="title">NobodyValley</h1>
<div id="agents"></div>
<script>
const colors = ["#5fd0e0", "#d08ae8", "#e8c75f", "#7fd67f", "#7f9fe8", "#e87f7f"];
const esc = s => (s || "").replace(/[&<>"]/g, c => ({"&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;"}[c]));
async function refresh() {
  let s;
  try { s = await (await fetch("/state")).json(); } catch (e) { return; }
  document.getElementById("title").textContent = s.world ? "NobodyValley - " + s.world : "NobodyValley";
  const names = Object.keys(s.agents);
  const color = n => colors[names.indexOf(n) % colors.length];
  document.getElementById("agents").innerHTML = names.map(n => {
    const a = s.agents[n];
    // The panels are re-rendered every poll; a negative delay keeps the spinner turning smoothly across that.
    const llm = a.llm ? `<span class="llm ${a.llm}"><span class="spin" style="animation-delay:-${(Date.now() % 800) / 1000}s"></span>` +
      `${a.llm === "waiting" ? "waiting for the model" : "thinking"} ${Math.max(0, Math.floor(s.now - a.llm_since))}s</span>` : "";
    return `<div class="agent" style="--c:${color(n)}">
      <span class="name">${esc(n)}</span>${llm}<span class="meta">${esc(a.where)}</span>
      <div class="doing clamp2">${esc(a.doing || "idle")}</div>
      ${a.thought ? `<div class="thought clamp3" title="${esc(a.thought)}">"${esc(a.thought)}"</div>` : ""}
      ${a.plan ? `<div class="plan clamp2" title="${esc(a.plan)}">Plan: ${esc(a.plan)}</div>` : ""}
      ${a.result ? `<div class="result clamp2 ${a.result_ok ? "" : "failed"}" title="${esc(a.result)}">${esc(a.result)}</div>` : ""}
    </div>`;
  }).join("");
}
refresh(); setInterval(refresh, 500);
</script>
</body></html>
"""


class _Server(ThreadingHTTPServer):
    daemon_threads = True

    def server_bind(self) -> None:
        # HTTPServer.server_bind() calls socket.getfqdn(), which can take ~30s on macOS.
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = "127.0.0.1", self.server_address[1]


class Dashboard:
    def __init__(self, port: int = 8780):
        self.port = port
        self.lock = threading.Lock()
        self.agents: dict[str, dict[str, Any]] = {}
        self.world = ""

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}/"

    # ---------------------------------------------------------------- updates (called from the agents)

    def update(self, name: str, **fields: Any) -> None:
        with self.lock:
            self.agents.setdefault(name, {}).update(fields)

    def set_world(self, text: str) -> None:
        with self.lock:
            self.world = text

    def state(self) -> dict[str, Any]:
        with self.lock:
            return {"agents": {n: dict(a) for n, a in self.agents.items()}, "world": self.world, "now": time.time()}

    # ---------------------------------------------------------------- serving

    def start(self) -> None:
        dashboard = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self) -> None:  # noqa: N802
                if self.path.startswith("/state"):
                    body, kind = json.dumps(dashboard.state()).encode(), "application/json"
                else:
                    body, kind = PAGE.encode(), "text/html; charset=utf-8"
                self.send_response(200)
                self.send_header("Content-Type", kind)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args: Any) -> None:
                pass

        server = _Server(("127.0.0.1", self.port), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()

    def open_window(self, geometry: str | None) -> None:
        """Open the dashboard in a native window (the system web view); geometry is "x,y,w,h" in screen points."""
        # Close a dashboard window left over from a previous run.
        subprocess.run(["pkill", "-f", "--", "python[^ ]* -m nobodyvalley[.]dashboard_window "], check=False)
        try:
            subprocess.Popen([sys.executable, "-m", "nobodyvalley.dashboard_window", self.url, geometry or ""],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError:
            if shutil.which("open"):
                subprocess.Popen(["open", self.url])
