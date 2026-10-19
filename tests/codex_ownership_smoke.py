"""Optional: python tests/codex_ownership_smoke.py <codex.exe>.

Runs two installed Codex processes in one isolated home/workspace against a
loopback response stub, then checks that the app identifies each owned history.
No account credentials, external model service, or generated tool calls are used.
Build SessionChecks in Release before running.
"""
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading


ready = threading.Event()
release = threading.Event()
requests = []


class Stub(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        requests.append(body)
        if len(requests) >= 2:
            ready.set()
        release.wait(50)
        item = {"id": "msg_fixture", "type": "message", "status": "completed", "role": "assistant",
                "content": [{"type": "output_text", "text": "FIXTURE_OK", "annotations": []}]}
        events = [
            {"type": "response.created", "response": {"id": "resp_fixture", "status": "in_progress", "output": []}},
            {"type": "response.output_item.done", "output_index": 0, "item": item},
            {"type": "response.completed", "response": {"id": "resp_fixture", "status": "completed", "output": [item],
                "usage": {"input_tokens": 1, "output_tokens": 1, "total_tokens": 2}}},
        ]
        payload = "".join(f"data: {json.dumps(event)}\n\n" for event in events).encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        try:
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError):
            pass


def main(executable):
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Stub)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    processes = []
    streams = []
    try:
        with tempfile.TemporaryDirectory(prefix="agentclihub-codex-owners-") as temp:
            root = Path(temp)
            assert root.resolve().parent == Path(tempfile.gettempdir()).resolve()
            home = root / "codex"
            cwd = root / "workspace"
            home.mkdir()
            cwd.mkdir()
            (home / "config.toml").write_text(
                'model_provider = "fixture"\nmodel = "fixture"\n'
                '[model_providers.fixture]\nname = "Local fixture"\n'
                f'base_url = "http://127.0.0.1:{server.server_port}/v1"\n'
                'wire_api = "responses"\nrequires_openai_auth = false\n', encoding="utf-8")
            env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("CODEX", "OPENAI"))}
            env.update(CODEX_HOME=str(home), HOME=str(root), USERPROFILE=str(root))
            try:
                for i in range(2):
                    stream = open(root / f"process-{i}.log", "w+b")
                    streams.append(stream)
                    processes.append(subprocess.Popen([str(Path(executable).resolve()), "exec", "--json",
                        "--disable", "plugins", "--disable", "remote_plugin",
                        "--skip-git-repo-check", "--sandbox", "read-only", f"FIXTURE_{i}"],
                        cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=stream, stderr=stream))
                assert ready.wait(35), "both native Codex processes must reach the loopback server"
                ids = []
                for stream in streams:
                    stream.seek(0)
                    rows = stream.read().decode("utf-8", errors="replace").splitlines()
                    started = [json.loads(row) for row in rows if row.startswith('{"type":"thread.started"')]
                    assert len(started) == 1, "each native process must report its own thread"
                    ids.append(started[0]["thread_id"])
                checker = Path(__file__).parent / "SessionChecks/bin/Release/net9.0-windows/SessionChecks.dll"
                subprocess.run(["dotnet", str(checker.resolve()), "--codex-owners", str(home), str(cwd),
                    str(processes[0].pid), ids[0], str(processes[1].pid), ids[1]], check=True, timeout=15)
                release.set()
                for process in processes:
                    assert process.wait(timeout=15) == 0, "native fixture response must complete"
                print("PASS two installed Codex sessions in one home/workspace retain distinct process ownership")
            finally:
                release.set()
                for process in processes:
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=5)
                for stream in streams:
                    stream.close()
    finally:
        release.set()
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


if __name__ == "__main__":
    main(sys.argv[1])
