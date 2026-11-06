import json
import queue
import threading
import unittest

from screenstocks_bridge import BridgeClient, MarketCandle, MarketClock
from test_client import _FakeBridge


def candle(revision=1, sequence=1):
    return {"generation": "source", "sequence": sequence, "stockId": "A", "bucketStartMs": 1000,
            "bucketEndMs": 2000, "revision": revision, "open": 10, "high": 12, "low": 9, "close": 11,
            "quoteCount": 4, "firstSourceTimeMs": 1100, "lastSourceTimeMs": 1900, "firstTick": 1, "lastTick": 4}


class MarketStreamClientTests(unittest.TestCase):
    def test_helpers_validate_controls_without_sending_invalid_requests(self):
        client = BridgeClient(token="test")
        sent = []
        client.request = lambda *args: sent.append(args) or {}
        for value in [0, -1, float("nan"), float("inf"), True]:
            with self.assertRaises(ValueError):
                client.set_market_rate(value)
        with self.assertRaises(ValueError):
            client.set_instant_mode("true")
        with self.assertRaises(ValueError):
            client.subscribe_candles(lambda _: None, after_sequence=-1)
        with self.assertRaises(ValueError):
            client.subscribe_candles(lambda _: None, candle_mode="filled")
        self.assertEqual(sent, [])
        client.set_market_rate(1000)
        client.set_instant_mode(True)
        client.market_stream_status()
        self.assertEqual(sent, [("market.set_rate", {"rateHz": 1000}),
                                ("market.set_instant", {"enabled": True}), ("market.stream_status",)])

    def test_connected_candles_explicitly_request_mode_and_expose_open_provenance(self):
        client = BridgeClient(token="test")
        sent = []
        client.request = lambda *args: sent.append(args) or {"candleModes": ["observed", "connected"]}
        client.subscribe_candles(lambda _: None, candle_mode="connected", generation="connected-generation", after_sequence=7)
        self.assertEqual(sent, [("market.stream_status",), ("market.candles.subscribe", {"generation": "connected-generation", "afterSequence": 7, "candleMode": "connected"})])
        bar = MarketCandle.from_dict({**candle(), "candleMode": "connected", "openProvenance": "previous_close"})
        self.assertEqual((bar.candle_mode, bar.open_provenance), ("connected", "previous_close"))
        bar = MarketCandle.from_dict(candle())
        self.assertEqual((bar.candle_mode, bar.open_provenance), ("observed", "source_observation"))

    def test_connected_mode_refuses_old_plugins_instead_of_silently_receiving_observed_candles(self):
        from screenstocks_bridge import BridgeError
        client = BridgeClient(token="test")
        sent = []
        client.request = lambda *args: sent.append(args) or {}
        with self.assertRaises(BridgeError) as error:
            client.subscribe_candles(lambda _: None, candle_mode="connected")
        self.assertEqual(error.exception.code, "unsupported_candle_mode")
        self.assertEqual(sent, [("market.stream_status",)])

    def test_wire_callbacks_preserve_raw_json_and_replay_candle_revisions(self):
        raw = '{ "type":"priceVector", "sequence":30, "data":{"prices":[12],"tick":4,"serverTimeMs":1900}}'
        def responder(request, stream):
            result = {"snapshotRateHz": 4, "instant": True}
            events = []
            if request["method"] == "state.subscribe":
                events = [{"event": "market.updated", "data": {"ready": True}},
                          {"event": "market.price", "data": {"sourceJson": raw},
                           "delivery": {"generation": "connection", "stream": "market.price", "sequence": 1, "droppedEvents": 0}}]
            if request["method"] == "market.candles.subscribe":
                result = {"generation": "source", "latestSequence": 2, "gap": True, "replayCount": 2}
                events = [{"event": "market.candle", "data": candle()},
                          {"event": "market.candle", "data": candle(2, 2)}]
            frames = [{"id": request["id"], "ok": True, "result": result, "error": None}] + events
            stream.write("".join(json.dumps(frame) + "\n" for frame in frames).encode())
            stream.flush()
        server = _FakeBridge(responder)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        client = BridgeClient(port=server.server_address[1], token="test")
        received = queue.Queue()
        try:
            client.connect()
            client.subscribe_market(received.put)
            self.assertEqual(received.get(timeout=2)["event"], "market.updated")
            quote = received.get(timeout=2)
            self.assertEqual(quote["data"]["sourceJson"], raw)
            self.assertNotIn("delivery", json.loads(quote["data"]["sourceJson"]))
            self.assertEqual(quote["delivery"]["sequence"], 1)
            result = client.subscribe_candles(received.put, generation="source", after_sequence=0)
            self.assertTrue(result["gap"])
            first = MarketCandle.from_dict(received.get(timeout=2)["data"])
            second = MarketCandle.from_dict(received.get(timeout=2)["data"])
            self.assertEqual(first.bucket_start_ms, second.bucket_start_ms)
            self.assertEqual((first.revision, second.revision), (1, 2))
            requests = [server.requests.get(timeout=2), server.requests.get(timeout=2)]
            self.assertEqual(requests[1]["params"], {"generation": "source", "afterSequence": 0})
            client.unsubscribe_candles()
        finally:
            client.close(); server.shutdown(); server.server_close(); thread.join(timeout=2)

    def test_clock_model_preserves_unknown_server_time(self):
        value = {"synchronized": False, "serverTimeMs": None, "marketTick": 10, "tickDurationMs": 100,
                 "marketEpochMs": 100000, "dividendIntervalMs": 60000, "nextDividendBoundaryMs": None,
                 "remainingMs": None, "cycleProgress": None}
        clock = MarketClock.from_dict(value)
        self.assertIsNone(clock.server_time_ms)
        self.assertIsNone(clock.next_dividend_boundary_ms)
        self.assertEqual(clock.dividend_interval_ms, 60000)

    def test_local_overflow_reports_the_evicted_stream_without_waiting_for_another_event(self):
        client = BridgeClient(token="test")
        received = queue.Queue()
        client._callbacks.append(received.put)
        for _ in range(client._events.maxsize):
            client._events.put_nowait({"event": "market.price", "data": {}})
        client._queue_event({"event": "market.clock", "data": {}})
        client.close()
        worker = threading.Thread(target=client._event_loop, daemon=True)
        worker.start()
        event = received.get(timeout=2)
        self.assertEqual(event["event"], "stream.loss")
        self.assertEqual(event["data"]["origin"], "python_client")
        self.assertEqual(event["data"]["droppedEvents"], {"market.price": 1})
        worker.join(timeout=2)
        self.assertFalse(worker.is_alive())


if __name__ == "__main__":
    unittest.main()
