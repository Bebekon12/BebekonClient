"""Loopback-only deterministic traffic fixture. Never shipped in the APK."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import sys
import time

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Length', '9')
        self.send_header('Connection', 'close')
        self.end_headers()
        for _ in range(9):
            self.wfile.write(b'V')
            self.wfile.flush()
            time.sleep(1)
    def log_message(self, *args):
        pass

ThreadingHTTPServer(('127.0.0.1', int(sys.argv[1])), Handler).serve_forever()
