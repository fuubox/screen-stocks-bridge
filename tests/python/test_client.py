from __future__ import annotations

import json
import queue
import socketserver
import threading
import time
import unittest
from pathlib import Path
from typing import Any

from screenstocks_bridge import BridgeClient, BridgeError, Stock


FIXTURE = Path(__file__).resolve().parents[1] / "fixtures" / "offline_summary_result.json"


class _BridgeHandler(socketserver.StreamRequestHandler):
    def handle(self) -> None:
        for raw_line in self.rfile:
            request = json.loads(raw_line.decode("utf-8"))
            self.server.requests.put(request)  # type: ignore[attr-defined]
            self.server.responder(request, self.wfile)  # type: ignore[attr-defined]


class _FakeBridge(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, responder: Any) -> None:
        self.requests: queue.Queue[dict[str, Any]] = queue.Queue()
        self.responder = responder
        super().__init__(("127.0.0.1", 0), _BridgeHandler)


class BridgeClientTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))

        def responder(request: dict[str, Any], stream: Any) -> None:
            if request["method"] == "forced.oversized":
                stream.write(
                    json.dumps(
                        {"id": request["id"], "ok": True, "result": {"payload": "x" * (128 * 1024 + 1)}, "error": None}
                    ).encode("utf-8")
                    + b"\n"
                )
                stream.flush()
                return
            if request["method"] == "forced.error":
                message = {
                    "id": request["id"],
                    "ok": False,
                    "result": None,
                    "error": {"code": "expected_error", "message": "bad input", "retryAfterMs": 125},
                }
                self._write_fragmented(stream, json.dumps(message, separators=(",", ":")) + "\n")
                return

            message = {
                "id": request["id"],
                "ok": True,
                "result": self.fixture if request["method"] == "offline_summary.snapshot" else
                    ({"status": "unsubscribed"} if request["method"] == "auto_actions.unsubscribe_toasts" else {"status": "subscribed"}),
                "error": None,
            }
            frame = json.dumps(message, separators=(",", ":")) + "\n"
            if request["method"] == "state.subscribe":
                frame += json.dumps({"event": "market.updated", "data": {"ready": True}}) + "\n"
            elif request["method"] == "auto_actions.subscribe_toasts":
                frame += json.dumps({
                    "event": "auto_action.toast",
                    "data": {
                        "text": "Executed Buy for $TECH",
                        "stockId": "$TECH",
                        "actionType": "Buy",
                        "condition": "Above",
                        "targetPrice": 125.5,
                    },
                }) + "\n"
            self._write_fragmented(stream, frame)

        self.server = _FakeBridge(responder)
        self.server_thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.server_thread.start()
        self.clients: list[BridgeClient] = []

    def tearDown(self) -> None:
        for client in self.clients:
            client.close()
        self.server.shutdown()
        self.server.server_close()
        self.server_thread.join(timeout=2)

    @staticmethod
    def _write_fragmented(stream: Any, frame: str) -> None:
        encoded = frame.encode("utf-8")
        for start in range(0, len(encoded), 7):
            stream.write(encoded[start : start + 7])
            stream.flush()
            time.sleep(0.001)

    def _client(self) -> BridgeClient:
        client = BridgeClient(port=self.server.server_address[1], token="test-token")
        client.connect()
        self.clients.append(client)
        return client

    def test_offline_summary_method_reads_shared_fixture_and_sends_auth(self) -> None:
        client = self._client()

        result = client.offline_summary()
        sent = self.server.requests.get(timeout=1)

        self.assertEqual(self.fixture, result)
        self.assertEqual("offline_summary.snapshot", sent["method"])
        self.assertEqual("test-token", sent["token"])
        self.assertEqual({}, sent["params"])

    def test_leaderboard_method_sends_mode_and_explicit_query_limits(self) -> None:
        client = self._client()

        result = client.leaderboard("current", radius=25)
        sent = self.server.requests.get(timeout=1)

        self.assertEqual({"status": "subscribed"}, result)
        self.assertEqual("leaderboard.snapshot", sent["method"])
        self.assertEqual({"mode": "current", "radius": 25}, sent["params"])

    def test_leaderboard_method_omits_unspecified_query_limits(self) -> None:
        client = self._client()

        client.leaderboard("clan_net_worth")
        sent = self.server.requests.get(timeout=1)

        self.assertEqual({"mode": "clan_net_worth"}, sent["params"])

    def test_remote_errors_preserve_code_message_and_retry_delay(self) -> None:
        client = self._client()

        with self.assertRaises(BridgeError) as raised:
            client.request("forced.error")

        self.assertEqual("expected_error", raised.exception.code)
        self.assertEqual("bad input", raised.exception.message)
        self.assertEqual(125, raised.exception.retry_after_ms)

    def test_oversized_server_frame_preserves_frame_too_large_error(self) -> None:
        client = self._client()

        with self.assertRaises(BridgeError) as raised:
            client.request("forced.oversized")

        self.assertEqual("frame_too_large", raised.exception.code)

    def test_market_subscription_delivers_events_to_callback(self) -> None:
        client = self._client()
        received = threading.Event()
        events: list[dict[str, Any]] = []

        def callback(event: dict[str, Any]) -> None:
            events.append(event)
            received.set()

        client.subscribe_market(callback)

        self.assertTrue(received.wait(timeout=2))
        self.assertEqual("market.updated", events[0]["event"])
        self.assertEqual({"ready": True}, events[0]["data"])


    def test_auto_action_toast_subscription_delivers_details_and_unsubscribes(self) -> None:
        client = self._client()
        received = threading.Event()
        events: list[dict[str, Any]] = []

        def callback(event: dict[str, Any]) -> None:
            events.append(event)
            received.set()

        result = client.subscribe_auto_action_toasts(callback)
        subscribe_request = self.server.requests.get(timeout=1)

        self.assertEqual({"status": "subscribed"}, result)
        self.assertEqual("auto_actions.subscribe_toasts", subscribe_request["method"])
        self.assertTrue(received.wait(timeout=2))
        self.assertEqual(
            {
                "event": "auto_action.toast",
                "data": {
                    "text": "Executed Buy for $TECH",
                    "stockId": "$TECH",
                    "actionType": "Buy",
                    "condition": "Above",
                    "targetPrice": 125.5,
                },
            },
            events[0],
        )

        self.assertEqual({"status": "unsubscribed"}, client.unsubscribe_auto_action_toasts())
        unsubscribe_request = self.server.requests.get(timeout=1)
        self.assertEqual("auto_actions.unsubscribe_toasts", unsubscribe_request["method"])


class StockModelTests(unittest.TestCase):
    def test_effective_max_volume_is_exact_and_distinct_from_other_volumes(self) -> None:
        stock = Stock.from_dict({
            "stockId": "$TECH",
            "name": "Tech",
            "price": 12.5,
            "unlocked": True,
            "maxVolume": 30000,
            "effectiveMaxVolume": "12345678901234567890",
            "availableShares": 120,
        })

        self.assertTrue(hasattr(stock, "effective_max_volume"))
        self.assertEqual(30000, stock.max_volume)
        self.assertEqual("12345678901234567890", stock.effective_max_volume)
        self.assertEqual(120, stock.available_shares)


if __name__ == "__main__":
    unittest.main()
