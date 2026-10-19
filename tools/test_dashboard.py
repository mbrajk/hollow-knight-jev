"""Offline HTTP boundary test using synthetic state; always stops its own server."""
import http.client
import os
import pathlib
import queue
import re
import subprocess
import tempfile
import threading

ROOT = pathlib.Path(__file__).resolve().parents[1]
DLL = ROOT / "controller/bin/Release/net10.0/JevController.dll"


def main():
    with tempfile.TemporaryDirectory(prefix="jev-dashboard-test-") as output:
        process = subprocess.Popen(
            ["dotnet", str(DLL), "watch", "--state", str(ROOT / "examples/synthetic-gruz.json"),
             "--output", output, "--seconds", "30"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        lines = queue.Queue()
        def read_lines():
            for line in process.stdout:
                lines.put(line)
        reader = threading.Thread(target=read_lines, daemon=True)
        reader.start()
        try:
            line = lines.get(timeout=15)
            match = re.search(r"http://127\.0\.0\.1:(\d+)/", line)
            if not match:
                raise RuntimeError("Dashboard did not report a loopback address")
            port = int(match[1])
            def status(headers):
                connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
                try:
                    connection.request("GET", "/data", headers=headers)
                    response = connection.getresponse()
                    response.read()
                    return response.status
                finally:
                    connection.close()
            expected = [("local request", {}, 200),
                        ("matching origin", {"Origin": f"http://127.0.0.1:{port}"}, 200),
                        ("foreign Host", {"Host": "untrusted.invalid"}, 403),
                        ("foreign Origin", {"Origin": "https://untrusted.invalid"}, 403),
                        ("null Origin", {"Origin": "null"}, 403)]
            for name, headers, wanted in expected:
                actual = status(headers)
                if actual != wanted:
                    raise AssertionError(f"{name}: expected {wanted}, got {actual}")
                print(f"PASS {name}: {actual}")
        finally:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            reader.join(timeout=2)
            process.stdout.close()


if __name__ == "__main__":
    main()
