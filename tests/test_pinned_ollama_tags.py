"""M0.8b-2a: filtered Ollama native catalog contracts, no live model calls."""
import http.client
import json
import os
import pathlib
import socket
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "m0"))
from ollama_bridge import serve_bridge
from pinned_openai import pinned_openai_handler, pinned_tags_response

MODEL, DIGEST = "qwen3.5:4b", "f" * 64


def entry(name=MODEL, digest=DIGEST):
    return {
        "name": name, "model": name,
        "modified_at": "2026-11-18T12:00:00Z", "size": 2641000000,
        "digest": digest,
        "details": {
            "format": "gguf", "family": "qwen3.5", "families": ["qwen3.5"],
            "parameter_size": "4B", "quantization_level": "Q4_K_M"
        },
    }


class FilteringTests(unittest.TestCase):
    def test_only_exact_pinned_model_is_disclosed(self):
        source = {"models": [entry("other:1", "1" * 64), entry(),
                             entry("private:2", "2" * 64)]}
        with mock.patch("pinned_openai.fetch_local_ollama",
                        return_value=json.dumps(source).encode()) as fetch:
            result = json.loads(pinned_tags_response(MODEL, DIGEST))
        self.assertEqual([x["name"] for x in result["models"]], [MODEL])
        self.assertEqual(result["models"][0]["model"], MODEL)
        self.assertEqual(result["models"][0]["digest"], DIGEST)
        self.assertEqual(result["models"][0]["details"]["family"], "qwen3.5")
        self.assertNotIn("other:1", json.dumps(result))
        self.assertNotIn("private:2", json.dumps(result))
        fetch.assert_called_once_with("/api/tags")

    def test_mismatched_or_duplicated_pin_fails_closed(self):
        inputs = [
            {"models": [entry(digest="0" * 64)]},
            {"models": [entry(), entry()]},
            {"models": [entry("other:1")]},
            {"models": []},
            {"models": "invalid"},
            {"models": [{**entry(), "model": "other:1"}]},
            {"models": [{**entry(), "details": "incorrect"}]},
            {"models": [{**entry(), "size": True}]},
        ]
        for value in inputs:
            with self.subTest(value=value), mock.patch(
                "pinned_openai.fetch_local_ollama",
                return_value=json.dumps(value).encode()
            ), self.assertRaises(ValueError):
                pinned_tags_response(MODEL, DIGEST)

    def test_extra_model_metadata_is_not_leaked(self):
        model = entry()
        model["private_context"] = "DO-NOT-DISCLOSE"
        model["details"]["extra"] = "DO-NOT-DISCLOSE"
        with mock.patch("pinned_openai.fetch_local_ollama",
                        return_value=json.dumps({"models": [model]}).encode()):
            encoded = pinned_tags_response(MODEL, DIGEST)
        self.assertNotIn(b"DO-NOT-DISCLOSE", encoded)


@unittest.skipUnless(os.name == "posix" and hasattr(socket, "AF_UNIX"),
                     "Unix domain socket required")
class UnixHandlerTests(unittest.TestCase):
    def request(self, method, path):
        with tempfile.TemporaryDirectory() as temp:
            private = pathlib.Path(temp) / "private"
            private.mkdir(mode=0o700)
            unix = private / "ollama.sock"
            data = json.dumps({"models": [entry(), entry("other:1")]}).encode()
            with mock.patch("pinned_openai.fetch_local_ollama",
                            return_value=data) as fetched, mock.patch(
                "pinned_openai.verify_model_digest", return_value=True
            ), mock.patch("pinned_openai.request_pinned_stream") as inference:
                with serve_bridge(unix, pinned_openai_handler(MODEL, DIGEST)):
                    conn = http.client.HTTPConnection("localhost")
                    def connect():
                        conn.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                        conn.sock.connect(str(unix))
                    conn.connect = connect
                    try:
                        conn.request(method, path)
                        response = conn.getresponse()
                        result = (response.status, response.read())
                    finally:
                        conn.close()
                return result, fetched.call_count, inference.call_count

    def test_native_tags_exposes_only_selected_model(self):
        (status, body), fetched, executed = self.request("GET", "/api/tags")
        self.assertEqual(status, 200)
        self.assertEqual(fetched, 1)
        self.assertEqual(executed, 0)
        self.assertEqual([m["name"] for m in json.loads(body)["models"]], [MODEL])

    def test_unneeded_metadata_and_inference_routes_denied(self):
        for method, path in [
            ("GET", "/api/version"),
            ("GET", "/api/tags?debug=1"),
            ("GET", "/api/show"),
            ("POST", "/api/show"),
            ("GET", "/api/delete"),
            ("GET", "/v1/models?all=true"),
        ]:
            with self.subTest(method=method, path=path):
                (status, _), fetched, executed = self.request(method, path)
                self.assertEqual((status, fetched, executed), (403, 0, 0))

    def test_openai_catalog_remains_pinned(self):
        (status, body), fetched, executed = self.request("GET", "/v1/models")
        self.assertEqual((status, fetched, executed), (200, 0, 0))
        self.assertEqual([m["id"] for m in json.loads(body)["data"]], [MODEL])


if __name__ == "__main__":
    unittest.main()
