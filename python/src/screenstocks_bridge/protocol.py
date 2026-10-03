"""Protocol constants and errors shared by the Python bridge client."""

MAX_FRAME_BYTES = 16 * 1024


class BridgeError(RuntimeError):
    """A transport, protocol, or game-side API error."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


class _ReaderStopped(Exception):
    pass
