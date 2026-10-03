from __future__ import annotations

import argparse
import json
import os
import time

from screenstocks_bridge import BridgeClient


def main() -> None:
    parser = argparse.ArgumentParser(description="Watch the Screen Stocks market over the in-memory bridge.")
    parser.add_argument("--host", default=os.getenv("SCREENSTOCKS_HOST", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.getenv("SCREENSTOCKS_PORT", "48721")))
    parser.add_argument("--token", default=os.getenv("SCREENSTOCKS_TOKEN", ""))
    args = parser.parse_args()

    with BridgeClient(args.host, args.port, args.token) as client:
        snapshot = client.snapshot()
        print(json.dumps(snapshot, indent=2))
        client.subscribe_market(lambda event: print(json.dumps(event, separators=(",", ":"))))
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
