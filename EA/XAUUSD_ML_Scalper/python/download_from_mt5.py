#!/usr/bin/env python3
"""
download_from_mt5.py

Pulls historical bar or tick data directly from a running MetaTrader 5
terminal via the official `MetaTrader5` Python package - an alternative to
manually dragging Scripts/XAUUSD_ML_Scalper/ExportHistoryCSV.mq5 onto a
chart. Output CSV schema for bars matches ExportHistoryCSV.mq5 exactly
(time,open,high,low,close,tick_volume), so it's a drop-in input for
train_model.py / backtest_simulator.py.

IMPORTANT - this only works on the same machine as a real MT5 terminal:
- The `MetaTrader5` pip package is a thin wrapper around the terminal's own
  API. It cannot reach a broker on its own; it talks to a terminal process
  already installed on this machine.
- It ships Windows-only wheels. It will not install/run in this repo's
  Linux dev sandbox - run this script from the Windows (or Wine) machine
  where your MT5 terminal lives.
- The terminal should already be running. If it's not logged in, pass
  --login/--password/--server to have this script log in for you (or just
  log in by hand first and omit those flags).

Setup:
    pip install -r requirements-mt5.txt

Usage:
    # last 90 days of M1 bars
    python download_from_mt5.py --symbol XAUUSD --timeframe M1 --days 90 \
        --out XAUUSD_M1_history.csv

    # explicit date range
    python download_from_mt5.py --symbol XAUUSD --timeframe M1 \
        --from 2026-04-01 --to 2026-07-01 --out XAUUSD_M1_history.csv

    # tick data for a date range (bid/ask/last/volume)
    python download_from_mt5.py --symbol XAUUSD --mode ticks \
        --from 2026-06-01 --to 2026-07-01 --out XAUUSD_ticks.csv
"""
import argparse
import sys
from datetime import datetime, timedelta, timezone

TIMEFRAME_NAMES = ["M1", "M5", "M15", "M30", "H1", "H4", "D1"]


def parse_date(s):
    return datetime.strptime(s, "%Y-%m-%d").replace(tzinfo=timezone.utc)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--symbol", default="XAUUSD")
    ap.add_argument("--mode", choices=["bars", "ticks"], default="bars")
    ap.add_argument("--timeframe", choices=TIMEFRAME_NAMES, default="M1",
                     help="bars mode only")
    ap.add_argument("--days", type=int, default=None,
                     help="pull the last N days up to now (alternative to --from/--to)")
    ap.add_argument("--from", dest="date_from", type=parse_date, default=None,
                     help="YYYY-MM-DD, UTC")
    ap.add_argument("--to", dest="date_to", type=parse_date, default=None,
                     help="YYYY-MM-DD, UTC (default: now)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--login", type=int, default=None)
    ap.add_argument("--password", default=None)
    ap.add_argument("--server", default=None)
    ap.add_argument("--terminal-path", default=None,
                     help="path to terminal64.exe, only needed if MT5 can't find it automatically")
    args = ap.parse_args()

    try:
        import MetaTrader5 as mt5
    except ImportError:
        print("ERROR: the MetaTrader5 package is not installed, or you are not on a "
              "machine that can run it (Windows-only wheels; won't install on Linux/macOS). "
              "Run this script on the Windows machine where your MT5 terminal is installed, "
              "after 'pip install -r requirements-mt5.txt'. Alternatively use "
              "Scripts/XAUUSD_ML_Scalper/ExportHistoryCSV.mq5 from inside MetaTrader instead.",
              file=sys.stderr)
        sys.exit(1)

    import pandas as pd

    init_kwargs = {}
    if args.terminal_path:
        init_kwargs["path"] = args.terminal_path
    if args.login:
        init_kwargs["login"] = args.login
        init_kwargs["password"] = args.password
        init_kwargs["server"] = args.server

    if not mt5.initialize(**init_kwargs):
        print(f"ERROR: mt5.initialize() failed: {mt5.last_error()}. "
              f"Is the MT5 terminal installed and (for login) reachable?", file=sys.stderr)
        sys.exit(1)

    try:
        if not mt5.symbol_select(args.symbol, True):
            print(f"ERROR: symbol '{args.symbol}' not found/couldn't be selected in Market Watch. "
                  f"Check the exact symbol name your broker uses (often e.g. 'XAUUSD', "
                  f"'XAUUSD.', 'GOLD', 'XAUUSDm' depending on broker).", file=sys.stderr)
            sys.exit(1)

        date_to = args.date_to or datetime.now(timezone.utc)
        if args.date_from:
            date_from = args.date_from
        elif args.days:
            date_from = date_to - timedelta(days=args.days)
        else:
            print("ERROR: specify either --days or --from/--to.", file=sys.stderr)
            sys.exit(1)

        if args.mode == "bars":
            timeframe = getattr(mt5, f"TIMEFRAME_{args.timeframe}")
            rates = mt5.copy_rates_range(args.symbol, timeframe, date_from, date_to)
            if rates is None or len(rates) == 0:
                print(f"ERROR: no bars returned ({mt5.last_error()}). Your broker may not "
                      f"retain history that far back for this timeframe - try a shorter range.",
                      file=sys.stderr)
                sys.exit(1)

            df = pd.DataFrame(rates)
            df["time"] = pd.to_datetime(df["time"], unit="s", utc=True)
            out = df[["time", "open", "high", "low", "close", "tick_volume"]].sort_values("time")
            out.to_csv(args.out, index=False)
            print(f"Wrote {len(out)} {args.timeframe} bars for {args.symbol} to {args.out} "
                  f"({out['time'].iloc[0]} .. {out['time'].iloc[-1]})")

        else:  # ticks
            ticks = mt5.copy_ticks_range(args.symbol, date_from, date_to, mt5.COPY_TICKS_ALL)
            if ticks is None or len(ticks) == 0:
                print(f"ERROR: no ticks returned ({mt5.last_error()}). Tick history retention "
                      f"is usually much shorter than bar history at most brokers - try a "
                      f"narrower date range.", file=sys.stderr)
                sys.exit(1)

            df = pd.DataFrame(ticks)
            df["time"] = pd.to_datetime(df["time_msc"], unit="ms", utc=True)
            out = df[["time", "bid", "ask", "last", "volume", "flags"]].sort_values("time")
            out.to_csv(args.out, index=False)
            print(f"Wrote {len(out)} ticks for {args.symbol} to {args.out} "
                  f"({out['time'].iloc[0]} .. {out['time'].iloc[-1]})")

    finally:
        mt5.shutdown()


if __name__ == "__main__":
    main()
