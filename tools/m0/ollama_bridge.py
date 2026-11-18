#!/usr/bin/env python3
"""M0.5 narrow Unix-socket bridge: local Ollama metadata only, never code execution.

The broker intentionally does NOT support inference/chat yet. This allows us
to prove that an isolated, network-disabled container can reach an explicitly
allowlisted host capability without acquiring normal host network access.
"""
from __future__ import annotations

import http.client
import http.server
import json
import os
import pathlib
import socket
import socketserver
import stat
import threading
from contextlib import contextmanager
from typing import Iterator

# Version 1 intentionally permits only harmless read-only Ollama metadata.
_ALLOWED_GET = frozenset(("/api/version", "/api/tags"))
_MAX_RESPONSE = 1048576


def allowed_request(method: str, path: str) -> bool:
    """Exact path match; never accept traversal, query strings, or POST."""
    return method == "GET" and path in _ALLOWED_GET


def fetch_local_ollama(path: str) -> bytes:
    """Use a fixed local destination without following redirects or DNS."""
    if path not in _ALLOWED_GET:
        raise ValueError("Unsupported Ollama resource")
    conn = http.client.HTTPConnection("127.0.0.1", 11434, timeout=8)
    try:
        conn.request("GET", path, headers={"Connection": "close"})
        reply = conn.getresponse()
        if reply.status != 200:
            raise RuntimeError(f"Ollama HTTP status {reply.status}")
        data = reply.read(_MAX_RESPONSE + 1)
        if len(data) > _MAX_RESPONSE:
            raise ValueError("Ollama response larger than maximum")
        json.loads(data)
        return data
    finally:
        conn.close()


class BridgeHandler(http.server.BaseHTTPRequestHandler):
    """HTTP over a dedicated AF_UNIX socket, with no generic proxy capability."""
    protocol_version = "HTTP/1.0"

    def address_string(self) -> str:
        return "sandbox-peer"

    def log_message(self, format: str, *args: object) -> None:
        pass

    def _reply(self, code: int, payload: bytes) -> None:
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)
        self.close_connection = True

    def do_GET(self) -> None:
        if not allowed_request("GET", self.path) or self.headers.get("Content-Length"):
            self._reply(403, b'{"error":"not permitted"}')
            return
        try:
            data = fetch_local_ollama(self.path)
        except (OSError, RuntimeError, ValueError, json.JSONDecodeError):
            self._reply(502, b'{"error":"local inference provider unavailable"}')
            return
        self._reply(200, data)

    def do_POST(self) -> None:
        self._reply(403, b'{"error":"not permitted"}')

    def do_PUT(self) -> None:
        self._reply(403, b'{"error":"not permitted"}')

    def do_DELETE(self) -> None:
        self._reply(403, b'{"error":"not permitted"}')

    def do_PATCH(self) -> None:
        self._reply(403, b'{"error":"not permitted"}')

    def do_CONNECT(self) -> None:
        self._reply(403, b'{"error":"not permitted"}')


@contextmanager
def serve_bridge(socket_path: pathlib.Path) -> Iterator[None]:
    """Host process owns the socket; directory must be private to its user."""
    if os.name != "posix" or not hasattr(socket, "AF_UNIX"):
        raise RuntimeError("Unix-socket broker requires POSIX AF_UNIX support")
    socket_path = socket_path.resolve(strict=False)
    parent = socket_path.parent
    if not parent.is_dir() or stat.S_IMODE(parent.stat().st_mode) & 0o077:
        raise ValueError("Broker directory must exist with mode 0700")
    if socket_path.exists() or socket_path.is_symlink():
        raise FileExistsError("Refusing to overwrite existing socket path")

    class UnixHTTPServer(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
        daemon_threads = True

    server = UnixHTTPServer(str(socket_path), BridgeHandler)
    try:
        os.chmod(socket_path, 0o600)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield
        finally:
            server.shutdown()
            thread.join(timeout=5)
    finally:
        server.server_close()
        socket_path.unlink(missing_ok=True)
