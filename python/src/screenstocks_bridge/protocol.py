"""Protocol constants and errors shared by the Python bridge client."""

MAX_FRAME_BYTES = 128 * 1024


class BridgeError(RuntimeError):
    """A transport, protocol, or game-side API error."""

    def __init__(self, code: str, message: str, retry_after_ms: int | None = None) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.retry_after_ms = retry_after_ms


class _ReaderStopped(Exception):
    pass
