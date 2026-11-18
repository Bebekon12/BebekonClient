"""Compare local HTTP transfers through a direct socket and through sing-box VLESS.

Usage: python scripts/measure-local-routing.py path/to/sing-box.exe
All endpoints bind to loopback. No installed client or user profile is used.
"""
import argparse
import http.client
import json
import math
import socket
import statistics
import subprocess
import tempfile
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


HOST = "127.0.0.1"
SIZE = 8 * 1024 * 1024
BLOCK = bytes(64 * 1024)


class Receiver(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Length", str(SIZE))
        self.end_headers()
        for _ in range(SIZE // len(BLOCK)):
            self.wfile.write(BLOCK)

    def log_message(self, *args):
        pass


def transfer(port, proxy=None):
    connection = http.client.HTTPConnection(HOST, proxy or port, timeout=10)
    if proxy:
        connection.set_tunnel(HOST, port)
    try:
        start = time.perf_counter()
        connection.request("GET", "/")
        response = connection.getresponse()
        assert response.status == 200
        first = response.read(1)
        first_byte = time.perf_counter()
        count = len(first)
        while block := response.read(len(BLOCK)):
            count += len(block)
        elapsed = time.perf_counter() - start
        if count != SIZE:
            raise IOError("Incomplete transfer")
        return (first_byte - start) * 1000, SIZE * 8 / elapsed / 1e6
    finally:
        connection.close()


def measure(executable, rounds):
    client_id = str(uuid.uuid4())
    with ThreadingHTTPServer((HOST, 0), Receiver) as receiver, tempfile.TemporaryDirectory() as work:
        # Reserve both ports simultaneously so that the kernel picks distinct numbers.
        with socket.socket() as a, socket.socket() as b:
            a.bind((HOST, 0))
            b.bind((HOST, 0))
            proxy, relay = a.getsockname()[1], b.getsockname()[1]
        cfg = {"log": {"level": "error"}, "inbounds": [
            {"type": "mixed", "tag": "client", "listen": HOST, "listen_port": proxy},
            {"type": "vless", "tag": "relay", "listen": HOST, "listen_port": relay, "users": [{"uuid": client_id}]}
        ], "outbounds": [
            {"type": "vless", "tag": "tunnel", "server": HOST, "server_port": relay, "uuid": client_id},
            {"type": "direct", "tag": "uplink"}
        ], "route": {"rules": [{"inbound": ["client"], "action": "route", "outbound": "tunnel"}], "final": "uplink"}}
        config = Path(work) / "benchmark.json"
        config.write_text(json.dumps(cfg), encoding="utf-8")
        samples = {"Direct": [], "VLESS": []}
        threading.Thread(target=receiver.serve_forever, daemon=True).start()
        core = subprocess.Popen([executable, "run", "-c", str(config)], cwd=work)
        try:
            end = time.monotonic() + 10
            while True:
                if core.poll() is not None:
                    raise RuntimeError("sing-box exited before the benchmark")
                try:
                    with socket.create_connection((HOST, proxy), timeout=.3):
                        break
                except OSError:
                    if time.monotonic() >= end:
                        raise
                    time.sleep(.05)
            for i in range(rounds + 1):
                order = ("Direct", "VLESS") if i % 2 == 0 else ("VLESS", "Direct")
                for name in order:
                    result = transfer(receiver.server_port, proxy if name == "VLESS" else None)
                    if i:  # One warm-up transfer per path.
                        samples[name].append(result)
        finally:
            core.terminate()
            core.wait(timeout=10)
            receiver.shutdown()
    for name, values in samples.items():
        latency, rate = zip(*values)
        p95 = sorted(latency)[math.ceil(.95 * len(latency)) - 1]
        print(f"{name}: n={len(values)}, first byte median={statistics.median(latency):.2f} ms, "
              f"p95={p95:.2f} ms, transfer median={statistics.median(rate):.1f} Mbit/s")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sing_box", type=Path)
    args = parser.parse_args()
    measure(str(args.sing_box.resolve(strict=True)), 20)
