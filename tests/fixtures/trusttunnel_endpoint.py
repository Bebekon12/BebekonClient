"""Owned WSL fixture: official endpoint plus HTTP and UDP targets reachable through it."""
import http.server
import json
import os
import pathlib
import signal
import socket
import subprocess
import sys
import threading
import time

root = pathlib.Path(sys.argv[1])
endpoint = sys.argv[2]
root.mkdir(parents=True, exist_ok=True)
(root / "pid").write_text(str(os.getpid()))
subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                "-keyout", str(root / "key.pem"), "-out", str(root / "cert.pem"),
                "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost"],
               check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
(root / "credentials.toml").write_text('[[client]]\nusername="fixture"\npassword="fixture-secret"\n')
port_socket = socket.socket()
port_socket.bind(("0.0.0.0", 0))
port = port_socket.getsockname()[1]
port_socket.close()
(root / "vpn.toml").write_text(f'''listen_address = "0.0.0.0:{port}"
ipv6_available = false
allow_private_network_connections = true
credentials_file = "{root}/credentials.toml"
[listen_protocols.http2]
[listen_protocols.quic]
''')
(root / "hosts.toml").write_text(f'''[[main_hosts]]
hostname = "localhost"
cert_chain_path = "{root}/cert.pem"
private_key_path = "{root}/key.pem"
allowed_sni = ["alias.fixture.invalid"]
''')

class Target(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = ("official-trusttunnel-fixture|" + self.client_address[0]).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass

http = http.server.ThreadingHTTPServer(("0.0.0.0", 0), Target)
threading.Thread(target=http.serve_forever, daemon=True).start()
udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
udp.bind(("0.0.0.0", 0))
def echo():
    while True:
        data, peer = udp.recvfrom(65536)
        udp.sendto(data + b"|" + peer[0].encode(), peer)
threading.Thread(target=echo, daemon=True).start()

log = open(root / "endpoint.log", "w")
process = subprocess.Popen([endpoint, str(root / "vpn.toml"), str(root / "hosts.toml"), "--jobs", "2", "--loglvl", "debug"], stdout=log, stderr=log)
def stop(*args):
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    sys.exit(0)
signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGINT, stop)
try:
    time.sleep(1)
    if process.poll() is not None:
        raise RuntimeError("Official endpoint failed; inspect endpoint.log")
    address = subprocess.check_output(["hostname", "-I"], text=True).split()[0]
    # Import an actual link exported by the official endpoint, not an encoder from our tests.
    exported = subprocess.check_output([endpoint, str(root / "vpn.toml"), str(root / "hosts.toml"),
                                    "-c", "fixture", "-a", f"{address}:{port}", "-n", "Official fixture",
                                    "-s", "alias.fixture.invalid"], text=True)
    link = next(line.strip() for line in exported.splitlines() if line.startswith("tt://"))
    (root / "fixture.json").write_text(json.dumps({"link": link, "address": address, "httpPort": http.server_port, "udpPort": udp.getsockname()[1]}))
    process.wait()
finally:
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=5)
