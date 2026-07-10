#!/usr/bin/env python3
"""
backtest_simulator.py

A bar-based (not tick-based) Python re-implementation of the EA's signal and
risk-management logic (RiskManager.mqh + the OnTick logic in the .mq5),
used to sanity-check the strategy's behaviour before ever running it in
MetaTrader. This is NOT a substitute for MetaTrader's own Strategy Tester -
it approximates fills using bar high/low instead of real tick data, and
uses a simplified constant-spread cost model. Use it to catch gross bugs in
the risk logic and to get a rough feel for drawdown behaviour, not as
proof of profitability.

Usage:
    python backtest_simulator.py --csv XAUUSD_M1_history.csv \
        --balance 100 --leverage 500
"""
import argparse

import numpy as np
import pandas as pd
from sklearn.neural_network import MLPClassifier

from train_model import compute_indicators, build_features, build_labels, HIDDEN1, HIDDEN2

CONTRACT_SIZE = 100.0  # troy oz per standard lot, XAUUSD convention at most brokers


def simulate(df, feat_cols, clf, args):
    balance = args.balance
    equity_curve = [balance]
    peak_equity = balance
    day_start_balance = balance
    current_day = None

    trades = []
    bars_since_trade = 1000
    halted = False
    daily_halted = False

    n = len(df)
    i = 0
    while i < n - args.horizon - 1:
        row = df.iloc[i]
        day = row["time"].date()
        if day != current_day:
            current_day = day
            day_start_balance = balance
            daily_halted = False

        bars_since_trade += 1
        equity = balance  # no open position between decision points in this simplified model
        peak_equity = max(peak_equity, equity)
        dd_pct = (peak_equity - equity) / peak_equity * 100.0 if peak_equity > 0 else 0.0

        if dd_pct >= args.max_drawdown_pct:
            halted = True

        daily_loss_pct = (day_start_balance - equity) / day_start_balance * 100.0 if day_start_balance > 0 else 0.0
        if daily_loss_pct >= args.max_daily_loss_pct:
            daily_halted = True

        if halted:
            i += 1
            continue

        if daily_halted or bars_since_trade < args.min_bars_between_trades:
            i += 1
            continue

        atr = row["atr"]
        if not np.isfinite(atr) or atr <= 0:
            i += 1
            continue

        x = row[feat_cols].astype(float).values.reshape(1, -1)
        if not np.all(np.isfinite(x)):
            i += 1
            continue

        prob = clf.predict_proba(x)[0, 1]
        confidence = abs(prob - 0.5) * 2.0
        if confidence < args.confidence_threshold:
            i += 1
            continue

        is_long = prob > 0.5
        sl_dist = atr * args.atr_sl_mult
        tp_dist = atr * args.atr_tp_mult
        entry = row["close"] + (args.spread / 2.0 if is_long else -args.spread / 2.0)

        risk_amount = balance * (args.risk_pct / 100.0)
        loss_per_lot = sl_dist * CONTRACT_SIZE
        lots = risk_amount / loss_per_lot if loss_per_lot > 0 else 0.0
        lots = np.floor(lots / 0.01) * 0.01
        lots = min(lots, args.max_lots)

        margin_required = (lots * CONTRACT_SIZE * entry) / args.leverage
        free_margin_budget = balance * (args.max_free_margin_usage_pct / 100.0)
        while margin_required > free_margin_budget and lots > 0.01:
            lots -= 0.01
            margin_required = (lots * CONTRACT_SIZE * entry) / args.leverage

        if lots < 0.01:
            i += 1
            continue

        sl_price = entry - sl_dist if is_long else entry + sl_dist
        tp_price = entry + tp_dist if is_long else entry - tp_dist

        exit_price = None
        exit_reason = "timeout"
        for j in range(1, args.max_holding_bars + 1):
            if i + j >= n:
                break
            fwd = df.iloc[i + j]
            if is_long:
                hit_sl = fwd["low"] <= sl_price
                hit_tp = fwd["high"] >= tp_price
            else:
                hit_sl = fwd["high"] >= sl_price
                hit_tp = fwd["low"] <= tp_price
            if hit_sl and hit_tp:
                exit_price, exit_reason = sl_price, "sl_tp_same_bar_assume_sl"
                break
            if hit_sl:
                exit_price, exit_reason = sl_price, "sl"
                break
            if hit_tp:
                exit_price, exit_reason = tp_price, "tp"
                break
        if exit_price is None:
            exit_price = df.iloc[min(i + args.max_holding_bars, n - 1)]["close"]

        pnl = (exit_price - entry) * (1 if is_long else -1) * lots * CONTRACT_SIZE
        balance += pnl
        equity_curve.append(balance)
        trades.append({
            "time": row["time"], "dir": "buy" if is_long else "sell",
            "lots": lots, "prob": prob, "pnl": pnl, "balance": balance,
            "reason": exit_reason,
        })
        bars_since_trade = 0

        if balance <= 0:
            print("ACCOUNT BLOWN (balance <= 0) - stopping simulation.")
            break

        i += 1

    return pd.DataFrame(trades), pd.Series(equity_curve)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--csv", required=True)
    ap.add_argument("--balance", type=float, default=100.0)
    ap.add_argument("--leverage", type=float, default=500.0)
    ap.add_argument("--risk-pct", type=float, default=1.5)
    ap.add_argument("--max-daily-loss-pct", type=float, default=6.0)
    ap.add_argument("--max-drawdown-pct", type=float, default=20.0)
    ap.add_argument("--max-free-margin-usage-pct", type=float, default=40.0)
    ap.add_argument("--confidence-threshold", type=float, default=0.65)
    ap.add_argument("--atr-sl-mult", type=float, default=1.5)
    ap.add_argument("--atr-tp-mult", type=float, default=2.5)
    ap.add_argument("--min-bars-between-trades", type=int, default=3)
    ap.add_argument("--max-holding-bars", type=int, default=60)
    ap.add_argument("--max-lots", type=float, default=1.0)
    ap.add_argument("--spread", type=float, default=0.35, help="round-turn spread in price units")
    ap.add_argument("--horizon", type=int, default=5, help="label horizon, must match training")
    ap.add_argument("--label-threshold", type=float, default=0.5)
    ap.add_argument("--test-size", type=float, default=0.2)
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args()

    df = pd.read_csv(args.csv, parse_dates=["time"]).sort_values("time").reset_index(drop=True)
    df = compute_indicators(df)
    feats = build_features(df)
    labels = build_labels(df, args.horizon, args.label_threshold)
    feat_cols = list(feats.columns)

    full = pd.concat([df, feats], axis=1)
    labeled = pd.concat([feats, labels.rename("label")], axis=1).dropna()

    split = int(len(labeled) * (1.0 - args.test_size))
    train_idx = labeled.index[:split]
    test_idx = labeled.index[split:]

    clf = MLPClassifier(hidden_layer_sizes=(HIDDEN1, HIDDEN2), activation="tanh",
                         solver="adam", alpha=1e-3, max_iter=500, random_state=args.seed,
                         early_stopping=True, n_iter_no_change=15)
    clf.fit(labeled.loc[train_idx, feat_cols].values, labeled.loc[train_idx, "label"].values)

    test_df = full.loc[test_idx].reset_index(drop=True)

    print(f"Simulating on {len(test_df)} out-of-sample bars, "
          f"starting balance={args.balance} leverage=1:{int(args.leverage)}")

    trades, equity = simulate(test_df, feat_cols, clf, args)

    if len(trades) == 0:
        print("No trades were taken (confidence threshold never reached, or halted immediately).")
        return

    wins = trades[trades["pnl"] > 0]
    losses = trades[trades["pnl"] <= 0]
    max_dd = ((equity.cummax() - equity) / equity.cummax() * 100.0).max()

    print(f"\nTrades: {len(trades)}  Win rate: {len(wins) / len(trades) * 100:.1f}%")
    print(f"Final balance: {trades['balance'].iloc[-1]:.2f}  "
          f"(start {args.balance:.2f}, {(trades['balance'].iloc[-1] / args.balance - 1) * 100:+.1f}%)")
    print(f"Max drawdown over run: {max_dd:.1f}%")
    print(f"Avg win: {wins['pnl'].mean() if len(wins) else 0:.3f}  "
          f"Avg loss: {losses['pnl'].mean() if len(losses) else 0:.3f}")
    print(f"Kill-switch / daily-halt aware simulation (caps enforced: "
          f"drawdown>={args.max_drawdown_pct}%, daily loss>={args.max_daily_loss_pct}%)")


if __name__ == "__main__":
    main()
