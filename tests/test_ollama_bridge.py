"""M0 bridge broker tests; never execute an agent or model-generated commands."""
import json
import os
import pathlib
import socket
import stat
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools" / "m0"))
from ollama_bridge import allowed_request, serve_bridge


class BrokerPolicyTests(unittest.TestCase):
    def test_metadata_paths_only(self):
        self.assertTrue(allowed_request("GET", "/api/version"))
        self.assertTrue(allowed_request("GET", "/api/tags"))
        for method, path in [
            ("GET", "/api/tags?foo=bar"), ("GET", "/api/tags/../delete"),
            ("GET", "/v1/models"), ("POST", "/api/chat"),
            ("POST", "/api/delete"), ("DELETE", "/api/tags"),
            ("GET", "http://example.com/api/version")]:
            self.assertFalse(allowed_request(method, path))

    @unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"), "Unix socket required")
    def test_private_mode_and_deny_write(self):
        with tempfile.TemporaryDirectory() as root:
            folder = pathlib.Path(root) / "private"
            folder.mkdir(mode=0o700)
            path = folder / "ollama.sock"
            with serve_bridge(path):
                self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                    client.connect(str(path))
                    client.sendall(b"POST /api/delete HTTP/1.0\r\nHost: local\r\n\r\n")
                    response = client.recv(2000)
                    self.assertIn(b"403", response)
            self.assertFalse(path.exists())

    @unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"), "Unix socket required")
    def test_allowed_metadata_broker(self):
        with tempfile.TemporaryDirectory() as root:
            folder = pathlib.Path(root) / "private"
            folder.mkdir(mode=0o700)
            path = folder / "ollama.sock"
            with mock.patch("ollama_bridge.fetch_local_ollama", return_value=b'{"version":"test"}') as fetch:
                with serve_bridge(path):
                    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                        client.connect(str(path))
                        client.sendall(b"GET /api/version HTTP/1.0\r\nHost: local\r\n\r\n")
                        response = bytearray()
                        while True:
                            chunk = client.recv(4096)
                            if not chunk:
                                break
                            response += chunk
                    self.assertIn(b"200 OK", response)
                    self.assertIn(b'{"version":"test"}', response)
                    fetch.assert_called_once_with("/api/version")

    @unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"), "Unix socket required")
    def test_broker_rejects_non_private_directory(self):
        with tempfile.TemporaryDirectory() as root:
            folder = pathlib.Path(root) / "shared"
            folder.mkdir(mode=0o755)
            with self.assertRaises(ValueError):
                with serve_bridge(folder / "ollama.sock"):
                    pass


if __name__ == "__main__":
    unittest.main()
