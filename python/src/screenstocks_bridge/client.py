"""Thread-safe JSON-lines client for the local Screen Stocks bridge."""

from __future__ import annotations

import json
import queue
import socket
import threading
import uuid
from collections.abc import Callable
from typing import Any

from .protocol import MAX_FRAME_BYTES, BridgeError, _ReaderStopped


class BridgeClient:
    """Connect to the in-game loopback bridge and issue state or trade calls."""

    def __init__(self, host: str = "127.0.0.1", port: int = 48721,
                 token: str = "", timeout: float = 5.0) -> None:
        self.host = host
        self.port = port
        self.token = token
        self.timeout = timeout
        self._socket: socket.socket | None = None
        self._send_lock = threading.Lock()
        self._pending_lock = threading.Lock()
        self._pending: dict[str, queue.Queue[dict[str, Any] | BaseException]] = {}
        self._callbacks_lock = threading.Lock()
        self._callbacks: list[Callable[[dict[str, Any]], None]] = []
        self._events: queue.Queue[dict[str, Any] | None] = queue.Queue(maxsize=128)
        self._trade_events: queue.Queue[dict[str, Any] | None] = queue.Queue(maxsize=128)
        self._closed = threading.Event()
        self._reader: threading.Thread | None = None
        self._event_worker: threading.Thread | None = None

    def connect(self) -> BridgeClient:
        """Open the authenticated TCP connection. Returns this client."""
        if self._socket is not None:
            return self
        if not self.token:
            raise ValueError("A bridge token is required.")
        sock = socket.create_connection((self.host, self.port), timeout=self.timeout)
        sock.settimeout(None)
        self._socket = sock
        self._closed.clear()
        self._reader = threading.Thread(target=self._read_loop, name="screenstocks-reader", daemon=True)
        self._event_worker = threading.Thread(target=self._event_loop, name="screenstocks-events", daemon=True)
        self._reader.start()
        self._event_worker.start()
        return self

    def request(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        """Send an RPC call and return its result object."""
        if self._socket is None or self._closed.is_set():
            raise BridgeError("not_connected", "Call connect() before making a request.")
        request_id = uuid.uuid4().hex
        waiter: queue.Queue[dict[str, Any] | BaseException] = queue.Queue(maxsize=1)
        with self._pending_lock:
            self._pending[request_id] = waiter
        frame = {
            "id": request_id,
            "method": method,
            "token": self.token,
            "params": params or {},
        }
        try:
            encoded = (json.dumps(frame, separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8")
            if len(encoded) - 1 > MAX_FRAME_BYTES:
                raise BridgeError("frame_too_large", "Request exceeds the 16 KiB frame limit.")
            with self._send_lock:
                if self._closed.is_set() or self._socket is None:
                    raise BridgeError("not_connected", "The bridge connection is closed.")
                self._socket.sendall(encoded)
            try:
                response = waiter.get(timeout=self.timeout)
            except queue.Empty as exc:
                raise BridgeError("timeout", "The bridge did not answer before the request timeout.") from exc
            if isinstance(response, BaseException):
                raise BridgeError("disconnected", str(response)) from response
            if not response.get("ok"):
                error = response.get("error") or {}
                retry_after_ms = error.get("retryAfterMs")
                if type(retry_after_ms) is not int or retry_after_ms <= 0:
                    retry_after_ms = None
                raise BridgeError(
                    str(error.get("code", "remote_error")),
                    str(error.get("message", "Bridge request failed.")),
                    retry_after_ms,
                )
            result = response.get("result")
            return result if isinstance(result, dict) else {}
        finally:
            with self._pending_lock:
                self._pending.pop(request_id, None)

    def snapshot(self) -> dict[str, Any]:
        """Return the current in-memory player and visible-market snapshot."""
        return self.request("state.snapshot")

    def offline_summary(self) -> dict[str, Any]:
        """Return the latest captured welcome-back summary, if one has appeared this session."""
        return self.request("offline_summary.snapshot")

    def human_activity(self, stock_id: str, limit: int = 32,
                       before_tick: int | None = None) -> dict[str, Any]:
        """Return one page of aggregate graph activity for a visible stock."""
        params: dict[str, Any] = {"stockId": stock_id, "limit": limit}
        if before_tick is not None:
            params["beforeTick"] = before_tick
        return self.request("market.human_activity", params)

    def subscribe_human_activity(self, stock_id: str,
                                 callback: Callable[[dict[str, Any]], None]) -> dict[str, Any]:
        """Subscribe to activity-page updates for one visible stock.

        The callback receives ``human_activity.updated`` events. Each event's
        data is a fresh page of up to 32 latest samples for the stock.
        Call this method again to subscribe to additional stocks.
        """
        with self._callbacks_lock:
            if callback not in self._callbacks:
                self._callbacks.append(callback)
        return self.request("market.human_activity.subscribe", {"stockId": stock_id})

    def unsubscribe_human_activity(self, stock_id: str) -> dict[str, Any]:
        """Stop receiving activity-page updates for one stock on this connection."""
        return self.request("market.human_activity.unsubscribe", {"stockId": stock_id})

    def set_human_activity_focus(self, stock_id: str) -> dict[str, Any]:
        """Override the game's activity feed to observe one stock.

        Focus selection is global to the running game and independent from
        subscribing to ``human_activity.updated`` events. Focus changes are
        limited to one every five seconds.
        """
        return self.request("market.human_activity.set_focus", {"stockId": stock_id})

    def clear_human_activity_focus(self) -> dict[str, Any]:
        """Clear the global activity-focus override and restore the prior game focus."""
        return self.request("market.human_activity.clear_focus")

    def subscribe_market(self, callback: Callable[[dict[str, Any]], None]) -> None:
        """Subscribe to market.updated and trade.completed event dictionaries."""
        with self._callbacks_lock:
            if callback not in self._callbacks:
                self._callbacks.append(callback)
        self.request("state.subscribe")

    def trade(self, action: str, stock_id: str, percent: float | None = None) -> dict[str, Any]:
        """Submit one explicit allowlisted trade; completion arrives as an event."""
        params: dict[str, Any] = {"action": action, "stockId": stock_id}
        if percent is not None:
            params["percent"] = percent
        return self.request("trade.submit", params)

    def upgrades(self) -> dict[str, Any]:
        """Return the game's dynamically discovered upgrade catalog and current levels."""
        return self.request("upgrades.snapshot")

    def purchase_upgrade(self, upgrade_id: str, quantity: int = 1) -> dict[str, Any]:
        """Ask the running game to process an upgrade through its in-process method.

        The bridge itself only talks to localhost. The game may communicate with its
        server in online mode; server state remains authoritative. The returned status
        is ``submitted``.
        Refresh :meth:`upgrades` to observe the resulting level.
        """
        return self.request("upgrades.purchase", {"upgradeId": upgrade_id, "quantity": quantity})

    def auto_actions(self) -> dict[str, Any]:
        """Return dynamically discovered auto-action slots and cooldown state."""
        return self.request("auto_actions.snapshot")

    def add_auto_action(self, stock_id: str, action_type: str, condition: str,
                        target_price: float, amount_percentage: int) -> dict[str, Any]:
        """Add an auto action in an available game slot and return its slot index."""
        return self.request("auto_actions.add", {
            "stockId": stock_id, "actionType": action_type, "condition": condition,
            "targetPrice": target_price, "amountPercentage": amount_percentage,
        })

    def update_auto_action(self, slot_index: int, stock_id: str, action_type: str,
                           condition: str, target_price: float,
                           amount_percentage: int) -> dict[str, Any]:
        """Replace an auto action's complete configuration by its discovered slot index."""
        return self.request("auto_actions.update", {
            "slotIndex": slot_index, "stockId": stock_id, "actionType": action_type,
            "condition": condition, "targetPrice": target_price,
            "amountPercentage": amount_percentage,
        })

    def remove_auto_action(self, slot_index: int) -> dict[str, Any]:
        """Remove the configured auto action occupying slot_index."""
        return self.request("auto_actions.remove", {"slotIndex": slot_index})

    def set_auto_action_enabled(self, slot_index: int, enabled: bool) -> dict[str, Any]:
        """Enable or disable one configured auto action."""
        return self.request("auto_actions.set_enabled", {"slotIndex": slot_index, "enabled": enabled})

    def set_auto_actions_active(self, active: bool) -> dict[str, Any]:
        """Set the game's global auto-action switch."""
        return self.request("auto_actions.set_active", {"active": active})

    def close(self) -> None:
        """Close the socket and stop its reader and event worker threads."""
        self._fail_pending(_ReaderStopped("Client closed."))
        self._closed.set()
        sock, self._socket = self._socket, None
        if sock is not None:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                sock.close()
            except OSError:
                pass
        try:
            self._events.put_nowait(None)
        except queue.Full:
            pass
        try:
            self._trade_events.put_nowait(None)
        except queue.Full:
            pass

    def _read_loop(self) -> None:
        buffer = bytearray()
        sock = self._socket
        try:
            if sock is None:
                return
            while not self._closed.is_set():
                chunk = sock.recv(4096)
                if not chunk:
                    raise _ReaderStopped("Bridge closed the connection.")
                buffer.extend(chunk)
                while True:
                    newline = buffer.find(b"\n")
                    if newline < 0:
                        if len(buffer) > MAX_FRAME_BYTES:
                            raise BridgeError("frame_too_large", "Bridge response exceeds the 16 KiB frame limit.")
                        break
                    if newline > MAX_FRAME_BYTES:
                        raise BridgeError("frame_too_large", "Bridge response exceeds the 16 KiB frame limit.")
                    raw = bytes(buffer[:newline]).rstrip(b"\r")
                    del buffer[:newline + 1]
                    message = json.loads(raw.decode("utf-8"))
                    if "event" in message:
                        self._queue_event(message)
                    else:
                        with self._pending_lock:
                            waiter = self._pending.get(str(message.get("id", "")))
                        if waiter is not None:
                            waiter.put(message)
        except BaseException as exc:
            if not self._closed.is_set():
                self._fail_pending(exc)
                self._closed.set()

    def _queue_event(self, event: dict[str, Any]) -> None:
        if event.get("event") == "trade.completed":
            # Preserve trade outcomes; a full completion queue applies TCP backpressure.
            self._trade_events.put(event)
            return
        try:
            self._events.put_nowait(event)
        except queue.Full:
            try:
                self._events.get_nowait()
            except queue.Empty:
                pass
            try:
                self._events.put_nowait(event)
            except queue.Full:
                pass

    def _event_loop(self) -> None:
        while True:
            if self._closed.is_set() and self._events.empty() and self._trade_events.empty():
                return
            try:
                event = self._trade_events.get_nowait()
            except queue.Empty:
                try:
                    event = self._events.get(timeout=0.25)
                except queue.Empty:
                    continue
            if event is None:
                continue
            with self._callbacks_lock:
                callbacks = tuple(self._callbacks)
            for callback in callbacks:
                try:
                    callback(event)
                except Exception:
                    continue

    def _fail_pending(self, error: BaseException) -> None:
        with self._pending_lock:
            waiters = tuple(self._pending.values())
        for waiter in waiters:
            try:
                waiter.put_nowait(error)
            except queue.Full:
                pass

    def __enter__(self) -> BridgeClient:
        return self.connect()

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.close()
