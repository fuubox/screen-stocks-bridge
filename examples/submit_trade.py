from __future__ import annotations

import argparse
import json
import os
import time

from screenstocks_bridge import BridgeClient

ACTIONS = (
    "buy_max", "buy_percent", "short_max", "short_percent", "close_max", "close_percent",
    "sell_max", "sell_percent", "cover_max", "cover_percent",
)


def main() -> None:
    parser = argparse.ArgumentParser(description="Submit one explicit Screen Stocks trade.")
    parser.add_argument("--host", default=os.getenv("SCREENSTOCKS_HOST", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.getenv("SCREENSTOCKS_PORT", "48721")))
    parser.add_argument("--token", default=os.getenv("SCREENSTOCKS_TOKEN", ""))
    parser.add_argument("--action", required=True, choices=ACTIONS)
    parser.add_argument("--stock-id", required=True)
    parser.add_argument("--percent", type=float)
    args = parser.parse_args()
    if args.action.endswith("_percent") and args.percent is None:
        parser.error("--percent is required for *_percent actions")
    if not args.action.endswith("_percent") and args.percent is not None:
        parser.error("--percent is only valid for *_percent actions")

    def show_event(event: dict[str, object]) -> None:
        print(json.dumps(event, separators=(",", ":")))

    with BridgeClient(args.host, args.port, args.token) as client:
        client.subscribe_market(show_event)
        receipt = client.trade(args.action, args.stock_id, args.percent)
        print(json.dumps(receipt))
        print("Trade submitted. Waiting for the game's completion event; this affects the shared online market.")
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
