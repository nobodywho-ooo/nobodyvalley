"""Chat backends: a local NobodyWho model (qwen3.5-9b, gemma4-e2b, gemma4-12b), or a remote
OpenAI-compatible endpoint (qwen3.8-flash-next on the ASCENT cluster, see ../on-prem).

Both implement the small surface agent.py uses (ask / reset / stats / tokenize), so the agent
loop itself doesn't care where the tokens come from. The local backend keeps the existing
behaviour: one nobodywho.ChatAsync per agent over one shared, loaded Model. The remote backend
keeps each agent's history here (the server is stateless between requests) and shapes the same
JSON-schema-constrained decision request over the OpenAI API, with vLLM's guided decoding in
place of NobodyWho's llguidance sampler.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any, Protocol

from nobodywho import ChatAsync, ContextShiftOptions, Model, SamplerPresets
from openai import AsyncOpenAI

# The vLLM deployment on the ASCENTs serves the model under this name
# (--served-model-name=default; LibreChat uses the same).
REMOTE_MODEL_NAME = "default"


def _estimate_tokens(text: str) -> int:
    """~4 chars per token, good enough for trimming and NV_DEBUG_TIMING estimates."""
    return max(1, len(text) // 4)


@dataclass
class Stats:
    context_used: int


class Backend(Protocol):
    """Provides one Chat per agent. Local backends load their own NobodyWho Model per chat
    (each agent runs its own weights); the remote backend is one shared vLLM client."""

    kind: str
    label: str  # shown when the agent starts

    async def create_chat(self, system_prompt: str, n_ctx: int) -> Chat: ...


class Chat(Protocol):
    """The chat surface used by Agent (mirrors nobodywho's ChatAsync API)."""

    async def ask(self, prompt: str) -> Any:
        """Ask a turn; returns a stream whose async next_token() yields tokens (None at the end)."""
        ...

    async def reset(self, system_prompt: str, history: list[dict[str, str]]) -> None:
        """Start a fresh context (new day). The `history` argument is unused, as in agent.py."""
        ...

    async def stats(self) -> Stats:
        """Token count of the current context (NV_DEBUG_TIMING only)."""
        ...

    async def tokenize(self, text: str) -> list[int]:
        """Tokenization (NV_DEBUG_TIMING only)."""
        ...


class LocalChat:
    """Delegates to a nobodywho ChatAsync, over the agent's own loaded model."""

    def __init__(self, chat: ChatAsync):
        self._chat = chat

    async def ask(self, prompt: str) -> Any:
        return self._chat.ask(prompt)

    async def reset(self, system_prompt: str, history: list[dict[str, str]]) -> None:
        await self._chat.reset(system_prompt, history)

    async def stats(self) -> Stats:
        return Stats(context_used=(await self._chat.stats()).context_used)

    async def tokenize(self, text: str) -> list[int]:
        return await self._chat.tokenize(text)


class LocalBackend:
    """One NobodyWho Model **per chat**: each agent loads (and keeps) its own copy of the
    weights on this Mac (Metal), so up to N agents share the GPU but not a model instance.
    Loading happens when the agent connects; pick a ref small enough that N copies fit."""

    kind = "local"

    def __init__(self, ref: str, schema: dict[str, Any]):
        self.ref = ref
        self.schema = schema
        self.label = f"{ref} (own model)"

    async def create_chat(self, system_prompt: str, n_ctx: int) -> LocalChat:
        model = await Model.load_model_async(self.ref) if hasattr(Model, "load_model_async") else Model(self.ref)
        return LocalChat(ChatAsync(
            model,
            n_ctx=n_ctx,
            system_prompt=system_prompt,
            sampler=SamplerPresets.constrain_with_json_schema(json.dumps(self.schema)),
            template_variables={"enable_thinking": False},  # Qwen chat templates; unused by others
            context_shift=ContextShiftOptions(keep_first_turns=0, keep_last_turns=4, target=0.6),
        ))


class RemoteChat:
    """One OpenAI-compatible chat for one agent, with the history kept here (vLLM has no
    per-request state). Mirrors the local chat: each turn's observation is a user message, the
    reply becomes the assistant message, and when the estimated context would grow past
    0.6 x n_ctx the oldest turns are dropped (keeping the last 4) - the same shape as the local
    ContextShiftOptions(keep_first_turns=0, keep_last_turns=4, target=0.6).
    """

    KEEP_TURNS = 4

    def __init__(self, client: AsyncOpenAI, model: str, system_prompt: str, n_ctx: int,
                 schema: dict[str, Any]):
        self._client = client
        self._model = model
        self._n_ctx = n_ctx
        # The ASCENTs' vLLM ships an llguidance-flavoured guided-decoding API: the schema goes in
        # a {"name", "schema"} wrapper (the openai-standard bare json_schema is rejected). The
        # schema itself keeps "x-guidance" (whitespace_flexible: false): it is honoured when the
        # server uses llguidance, and harmlessly ignored (spaced JSON) when a request carries
        # chat_template_kwargs and the server falls back.
        self._response_format = {
            "type": "json_schema",
            "json_schema": {"name": "decision", "schema": schema},
        }
        self._messages: list[dict[str, str]] = [{"role": "system", "content": system_prompt}]
        self._est_tokens = _estimate_tokens(system_prompt)

    async def ask(self, prompt: str) -> "_RemoteStream":
        self._messages.append({"role": "user", "content": prompt})
        try:
            stream = await self._client.chat.completions.create(
                model=self._model,
                messages=self._messages,
                stream=True,
                max_tokens=1024,  # the schema keeps decisions short; this is just a ceiling
                temperature=0.8,  # close to the local sampler's default
                # Same JSON-schema-constrained decision as the local chat.
                response_format=self._response_format,
                # The ASCENTs' vLLM defaults to thinking at medium effort; a decision turn isn't
                # a reasoning showpiece, so turn it off (the local chat sets the same flag).
                extra_body={"chat_template_kwargs": {"enable_thinking": False}},
            )
        except Exception:
            self._messages.pop()  # don't leave a user message without a reply
            raise
        return _RemoteStream(stream, self)

    def _record_reply(self, text: str) -> None:
        self._messages.append({"role": "assistant", "content": text})
        self._est_tokens += _estimate_tokens(text)
        self._trim()

    def _trim(self) -> None:
        """Drop the oldest turns until the estimated context fits the target (keeping 4)."""
        target = int(self._n_ctx * 0.6)
        # 1 system message + 2 per turn: keep >= KEEP_TURNS turns (>= 9 messages).
        while self._est_tokens > target and len(self._messages) > 1 + 2 * self.KEEP_TURNS:
            dropped = self._messages.pop(1)  # oldest user message
            self._est_tokens -= _estimate_tokens(dropped["content"])
            if len(self._messages) > 1 and self._messages[1]["role"] == "assistant":
                dropped = self._messages.pop(1)
                self._est_tokens -= _estimate_tokens(dropped["content"])

    async def reset(self, system_prompt: str, history: list[dict[str, str]]) -> None:
        # No server-side KV state to reset; just start a fresh context. The system prompt is
        # identical every day, so the server's prefix cache keeps it warm.
        self._messages = [{"role": "system", "content": system_prompt}]
        self._est_tokens = _estimate_tokens(system_prompt)

    async def stats(self) -> Stats:
        return Stats(context_used=self._est_tokens)

    async def tokenize(self, text: str) -> list[int]:
        # The real tokenizer lives on the server; char/4 is close enough for the prefill numbers.
        return [0] * _estimate_tokens(text)


class _RemoteStream:
    """Adapts openai's AsyncStream to the nobodywho-style next_token() API."""

    def __init__(self, stream: Any, chat: RemoteChat):
        self._it = stream.__aiter__()
        self._chat = chat
        self._parts: list[str] = []

    async def next_token(self) -> str | None:
        while True:
            try:
                chunk = await self._it.__anext__()
            except StopAsyncIteration:
                self._chat._record_reply("".join(self._parts))
                return None
            # vLLM may send chunks with no choices (usage) or empty content (finish_reason).
            if not chunk.choices:
                continue
            text = chunk.choices[0].delta.content
            if not text:
                continue
            self._parts.append(text)
            return text


class RemoteBackend:
    """Qwen3.8-Flash-Next on the ASCENT cluster (../on-prem), over the OpenAI-compatible API.
    One backend (one client) is shared by every farmer on the remote model: the "model" lives
    on the server, so there is nothing to load per agent."""

    kind = "remote"
    url: str

    def __init__(self, url: str, api_key: str, schema: dict[str, Any]):
        self.url = url
        self.schema = schema
        self.label = f"qwen3.8-flash-next on the ASCENTs ({url})"
        # No retries: a down ASCENT box fails a turn fast (the agent loop skips the turn) instead
        # of stacking up to 3 x timeout per ask.
        self._client = AsyncOpenAI(base_url=url, api_key=api_key, timeout=60.0, max_retries=0)

    async def create_chat(self, system_prompt: str, n_ctx: int) -> RemoteChat:
        return RemoteChat(self._client, REMOTE_MODEL_NAME, system_prompt, n_ctx, self.schema)

    async def check(self) -> None:
        """Fail fast (before the games start): proves the URL, the API key and the model name."""
        models = await self._client.models.list()
        names = [m.id for m in models.data]
        if REMOTE_MODEL_NAME not in names:
            raise RuntimeError(
                f"served model {REMOTE_MODEL_NAME!r} not found (the ASCENTs serve: "
                f"{', '.join(names) or 'nothing'})")