import json
import queue
import threading
import unittest

from screenstocks_bridge import ActivityReplica, ActivityResyncRequired, ActivitySnapshot


def page(**changes):
    return {"schemaVersion": 2, "snapshotId": "snap", "generation": "session", "revision": 1,
            "offset": 0, "nextOffset": None, "rows": [],
            "state": {"stockId": "A", "focusMode": "follow_graph", "ownerSessionId": None,
                      "hasAcceptedData": True}, **changes}


def chunk(**changes):
    return {"schemaVersion": 2, "generation": "session", "revision": 2, "previousRevision": 1,
            "chunkIndex": 0, "chunkCount": 1, "changes": [], "state": page()["state"], **changes}


class ActivityV2Tests(unittest.TestCase):
    def test_snapshot_rejects_mixed_revisions_incomplete_or_inconsistent_pages(self):
        first = page(rows=[{"key": "a", "value": {"kind": "sample"}}], nextOffset=1)
        for second in (page(offset=1, revision=2), page(offset=1, snapshotId="other"),
                       page(offset=1, state={"stockId": "B"}), page(offset=2)):
            with self.assertRaises(ActivityResyncRequired):
                ActivitySnapshot.from_pages([first, second])
        with self.assertRaises(ActivityResyncRequired):
            ActivitySnapshot.from_pages([first])
        self.assertEqual(ActivitySnapshot.from_pages([first, page(offset=1)]).revision, 1)

    def test_changes_commit_atomically_after_last_chunk_and_replace_not_sum(self):
        snapshot = ActivitySnapshot.from_pages([page(rows=[{"key": "old", "value": {"kind": "sample"}}])])
        replica = ActivityReplica(snapshot)
        first = chunk(chunkCount=2, changes=[{"op": "upsert", "key": "new", "value": {"kind": "sample", "up": 7}}])
        self.assertFalse(replica.apply(first))
        self.assertEqual(replica.snapshot.rows, snapshot.rows)
        self.assertTrue(replica.apply(chunk(chunkIndex=1, chunkCount=2, changes=[{"op": "remove", "key": "old"}])))
        self.assertEqual(replica.snapshot.rows, {"new": {"kind": "sample", "up": 7}})
        self.assertFalse(replica.apply(first))  # replay already represented by snapshot

    def test_generation_gap_order_and_chunk_metadata_mismatch_require_resync(self):
        invalid = [chunk(generation="new"), chunk(previousRevision=0), chunk(revision=3),
                   chunk(chunkIndex=1), chunk(schemaVersion=1), chunk(chunkCount=0)]
        for change in invalid:
            replica = ActivityReplica(ActivitySnapshot.from_pages([page()]))
            with self.assertRaises(ActivityResyncRequired):
                replica.apply(change)
            with self.assertRaises(ActivityResyncRequired):
                replica.apply(chunk())
        replica = ActivityReplica(ActivitySnapshot.from_pages([page()]))
        replica.apply(chunk(chunkCount=2))
        with self.assertRaises(ActivityResyncRequired):
            replica.apply(chunk(chunkIndex=1, chunkCount=2, state={"stockId": "B"}))

    def test_typed_views_keep_bucket_placement_and_unfocused_status_distinct(self):
        bucket = {"kind": "bucket", "bucketId": 10, "up": 0, "down": 0,
                  "upImpact": 0, "downImpact": 0, "placementSampleTick": 105}
        snapshot = ActivitySnapshot.from_pages([page(rows=[{"key": "bucket:10:0", "value": bucket}])])
        self.assertEqual(snapshot.buckets[0].placement_sample_tick, 105)
        self.assertTrue(snapshot.is_observing("A"))
        self.assertFalse(snapshot.is_observing("B"))
        bucket["up"] = 999
        self.assertEqual(snapshot.buckets[0].up, 0)

    def test_typed_snapshot_keeps_observation_evidence_without_reusing_old_ages_after_changes(self):
        snapshot = ActivitySnapshot.from_pages([page(observedAtUtc="2026-11-05T22:00:00Z",
                                                    responseAgeSeconds=7.5, valueChangeAgeSeconds=None)])
        self.assertEqual(snapshot.response_age_seconds, 7.5)
        self.assertIsNone(snapshot.value_change_age_seconds)
        replica = ActivityReplica(snapshot)
        replica.apply(chunk())
        self.assertIsNone(replica.snapshot.response_age_seconds)

    def test_client_methods_negotiate_v2_and_continue_the_same_snapshot(self):
        from screenstocks_bridge import BridgeClient
        client = BridgeClient(token="test")
        calls = []
        def request(method, args):
            calls.append((method, args))
            if method == "activity.snapshot":
                return page(offset=args["offset"])
            return page()
        client.request = request
        initial = page(rows=[{"key": "a", "value": {"kind": "sample"}}], nextOffset=1)
        client.activity_snapshot(initial)
        self.assertEqual(calls[-1], ("activity.snapshot", {"schemaVersion": 2, "snapshotId": "snap", "offset": 1}))
        client.pin_activity("A")
        client.takeover_activity("B")
        client.unpin_activity()
        self.assertEqual([call[0] for call in calls[-3:]], ["activity.pin", "activity.takeover", "activity.unpin"])
        self.assertTrue(all(call[1]["schemaVersion"] == 2 for call in calls))

    def test_quiet_queue_overflow_closes_and_delivers_resync_notification(self):
        from screenstocks_bridge import BridgeClient
        client = BridgeClient(token="test")
        client._activity_subscribed = True
        for i in range(client._events.maxsize):
            client._events.put_nowait({"event": "activity.changed", "data": {"revision": i}})
        client._queue_event({"event": "market.updated", "data": {}})
        self.assertTrue(client._closed.is_set())
        self.assertEqual(client._events.get_nowait()["event"], "activity.resync_required")
        self.assertIsNone(client._events.get_nowait())  # worker shutdown sentinel
        self.assertTrue(client._events.empty())

    def test_wire_subscription_initial_state_and_chunked_revisions(self):
        from test_client import _FakeBridge
        from screenstocks_bridge import BridgeClient, BridgeError
        def respond(request, stream):
            if request["method"] == "activity.takeover":
                response = {"id": request["id"], "ok": False, "result": None,
                            "error": {"code": "rate_limited", "message": "Wait", "retryAfterMs": 200}}
            else:
                response = {"id": request["id"], "ok": True, "result": page(), "error": None}
            frames = [response]
            if request["method"] == "activity.subscribe":
                frames += [{"event": "activity.changed", "data": chunk(chunkCount=2,
                            changes=[{"op": "upsert", "key": "sample:10", "value": {"kind": "sample", "up": 2}}])},
                           {"event": "activity.changed", "data": chunk(chunkIndex=1, chunkCount=2)}]
            stream.write("".join(json.dumps(frame) + "\n" for frame in frames).encode())
            stream.flush()
        server = _FakeBridge(respond)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        events = queue.Queue()
        client = BridgeClient(port=server.server_address[1], token="test-token")
        try:
            client.connect()
            initial = client.subscribe_activity(events.put)
            replica = ActivityReplica(client.activity_snapshot(initial))
            self.assertFalse(replica.apply(events.get(timeout=2)["data"]))
            self.assertTrue(replica.apply(events.get(timeout=2)["data"]))
            self.assertEqual(replica.snapshot.rows["sample:10"]["up"], 2)
            request = server.requests.get(timeout=2)
            self.assertEqual(request["token"], "test-token")
            self.assertEqual(request["params"], {"schemaVersion": 2})
            with self.assertRaises(BridgeError) as error:
                client.takeover_activity("B")
            self.assertEqual(error.exception.retry_after_ms, 200)
            client.unsubscribe_activity()
        finally:
            client.close()
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
