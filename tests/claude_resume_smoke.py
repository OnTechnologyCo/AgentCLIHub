"""Optional integration check: python tests/claude_resume_smoke.py <claude.exe>.

Uses generated transcripts, isolated profiles, a dummy API key, and a loopback
API stub. No real account or model service is used.
"""
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import uuid


requests = []


class Stub(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        requests.append((self.path, body))
        if "count_tokens" in self.path:
            payload = json.dumps({"input_tokens": 20}).encode()
            content_type = "application/json"
        else:
            message = {"id": "msg_fixture", "type": "message", "role": "assistant",
                       "model": body.get("model", "claude-sonnet-4-6"),
                       "content": [{"type": "text", "text": "FIXTURE_RESUMED"}],
                       "stop_reason": "end_turn", "stop_sequence": None,
                       "usage": {"input_tokens": 20, "output_tokens": 5}}
            if body.get("stream"):
                start = {**message, "content": [], "stop_reason": None}
                events = [
                    ("message_start", {"message": start}),
                    ("content_block_start", {"index": 0, "content_block": {"type": "text", "text": ""}}),
                    ("content_block_delta", {"index": 0, "delta": {"type": "text_delta", "text": "FIXTURE_RESUMED"}}),
                    ("content_block_stop", {"index": 0}),
                    ("message_delta", {"delta": {"stop_reason": "end_turn", "stop_sequence": None}, "usage": {"output_tokens": 5}}),
                    ("message_stop", {})]
                payload = "".join(f"event: {name}\ndata: {json.dumps({'type': name, **data})}\n\n" for name, data in events).encode()
                content_type = "text/event-stream"
            else:
                payload = json.dumps(message).encode()
                content_type = "application/json"
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


def main(executable):
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Stub)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix="agentclihub-claude-smoke-") as temp:
            root = Path(temp)
            cwd = root / "workspace"
            target = root / "target-home"
            source = root / "source-home" / "projects" / "custom-project"
            for folder in (cwd, target, source, root / "local", root / "roaming"):
                folder.mkdir(parents=True, exist_ok=True)
            session = str(uuid.uuid4())
            transcript = source / f"{session}.jsonl"
            parent = str(uuid.uuid4())
            rows = [
                {"uuid": parent, "parentUuid": None, "type": "user", "message": {"role": "user", "content": "FIXTURE_PREVIOUS_QUESTION"}},
                {"uuid": str(uuid.uuid4()), "parentUuid": parent, "type": "assistant",
                 "message": {"id": "msg_previous", "role": "assistant", "type": "message", "model": "<synthetic>",
                             "content": [{"type": "text", "text": "FIXTURE_PREVIOUS_ANSWER"}],
                             "stop_reason": "end_turn", "stop_sequence": None, "usage": {"input_tokens": 1, "output_tokens": 1}}}]
            for row in rows:
                row.update(cwd=str(cwd), sessionId=session, version="2.1.303", isSidechain=False,
                           userType="external", timestamp="2026-10-19T00:00:00.000Z")
            transcript.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
            env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("CLAUDE", "ANTHROPIC"))}
            env.update(CLAUDE_CONFIG_DIR=str(target), HOME=str(root), USERPROFILE=str(root),
                       LOCALAPPDATA=str(root / "local"), APPDATA=str(root / "roaming"),
                       ANTHROPIC_API_KEY="fixture-key", ANTHROPIC_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC="1", DISABLE_AUTOUPDATER="1")
            result = subprocess.run([str(Path(executable).resolve()), "--print", "--resume", str(transcript),
                                     "--setting-sources", "", "--tools", "", "--strict-mcp-config", "--model", "claude-sonnet-4-6",
                                     "--max-turns", "1", "FIXTURE_NEXT_QUESTION"],
                                    cwd=cwd, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
            if result.returncode or "FIXTURE_RESUMED" not in result.stdout:
                raise AssertionError(f"CLI exit {result.returncode}: {result.stdout[-2000:]} {result.stderr[-2000:]}")
            messages = [json.dumps(body.get("messages", [])) for path, body in requests if "count_tokens" not in path]
            assert any(all(marker in message for marker in ("FIXTURE_PREVIOUS_QUESTION", "FIXTURE_PREVIOUS_ANSWER", "FIXTURE_NEXT_QUESTION"))
                       for message in messages), "resumed API request lost previous conversation"
            print("PASS installed Claude CLI resumes the full transcript from a separate account home via absolute path")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


if __name__ == "__main__":
    main(sys.argv[1])
