#!/usr/bin/env python3
"""
train_model.py

Trains the small MLP (12 -> 8 -> 4 -> 1) used by MLModel.mqh on XAUUSD M1
history and exports the learned weights as an MQL5 header file.

Input: a CSV produced by MQL5/Scripts/XAUUSD_ML_Scalper/ExportHistoryCSV.mq5
       (columns: time,open,high,low,close,tick_volume ; oldest row first).

The feature engineering below MUST mirror
MQL5/Include/XAUUSD_ML_Scalper/FeatureEngine.mqh exactly. If you change one
side, change the other and retrain - a mismatch silently feeds the live
model data it never saw in training.

Usage:
    pip install -r requirements.txt
    python train_model.py --csv XAUUSD_M1_history.csv --output MLModel_trained.mqh
"""
import argparse
import sys

import numpy as np
import pandas as pd
from sklearn.neural_network import MLPClassifier
from sklearn.metrics import accuracy_score, precision_score, recall_score, confusion_matrix

HIDDEN1 = 8   # must match ML_H1_SIZE in MLModel.mqh
HIDDEN2 = 4   # must match ML_H2_SIZE in MLModel.mqh
N_FEATURES = 12  # must match ML_INPUT_SIZE in MLModel.mqh


def clamp(series, lo, hi):
    return series.clip(lower=lo, upper=hi)


def wilder_ema(series, period):
    return series.ewm(alpha=1.0 / period, adjust=False).mean()


def compute_indicators(df, rsi_period=14, macd_fast=12, macd_slow=26, macd_signal=9,
                        bb_period=20, bb_dev=2.0, atr_period=14,
                        stoch_k=14, stoch_d=3, stoch_slowing=3,
                        ema_fast=8, ema_slow=21, ema200=200):
    close = df["close"]
    high = df["high"]
    low = df["low"]

    # RSI (Wilder smoothing, matches MT5 iRSI)
    delta = close.diff()
    gain = delta.clip(lower=0.0)
    loss = -delta.clip(upper=0.0)
    avg_gain = wilder_ema(gain, rsi_period)
    avg_loss = wilder_ema(loss, rsi_period)
    rs = avg_gain / avg_loss.replace(0.0, np.nan)
    df["rsi"] = 100.0 - (100.0 / (1.0 + rs))
    df["rsi"] = df["rsi"].fillna(50.0)

    # MACD
    ema_f = close.ewm(span=macd_fast, adjust=False).mean()
    ema_s = close.ewm(span=macd_slow, adjust=False).mean()
    df["macd_main"] = ema_f - ema_s
    df["macd_signal"] = df["macd_main"].ewm(span=macd_signal, adjust=False).mean()

    # Bollinger Bands (population std, matches MT5 iBands)
    mid = close.rolling(bb_period).mean()
    std = close.rolling(bb_period).std(ddof=0)
    df["bb_upper"] = mid + bb_dev * std
    df["bb_lower"] = mid - bb_dev * std

    # ATR (Wilder smoothing, matches MT5 iATR)
    prev_close = close.shift(1)
    tr = pd.concat([
        high - low,
        (high - prev_close).abs(),
        (low - prev_close).abs(),
    ], axis=1).max(axis=1)
    df["atr"] = wilder_ema(tr, atr_period)

    # Stochastic (SMA mode, matches MT5 iStochastic MODE_SMA)
    lowest = low.rolling(stoch_k).min()
    highest = high.rolling(stoch_k).max()
    raw_k = (close - lowest) / (highest - lowest).replace(0.0, np.nan) * 100.0
    df["stoch_main"] = raw_k.rolling(stoch_slowing).mean()
    df["stoch_signal"] = df["stoch_main"].rolling(stoch_d).mean()

    # EMAs
    df["ema_fast"] = close.ewm(span=ema_fast, adjust=False).mean()
    df["ema_slow"] = close.ewm(span=ema_slow, adjust=False).mean()
    df["ema200"] = close.ewm(span=ema200, adjust=False).mean()

    df["vol_avg20"] = df["tick_volume"].rolling(20).mean()
    df["close_lag10"] = close.shift(10)

    return df


def build_features(df):
    atr = df["atr"]
    close = df["close"]

    pct_b = (close - df["bb_lower"]) / (df["bb_upper"] - df["bb_lower"]).replace(0.0, np.nan)
    pct_b = pct_b.fillna(0.5)

    f = pd.DataFrame(index=df.index)
    f["f0"] = (df["rsi"] / 100.0 - 0.5) * 2.0
    f["f1"] = clamp(df["macd_main"] / atr, -3.0, 3.0) / 3.0
    f["f2"] = clamp(df["macd_signal"] / atr, -3.0, 3.0) / 3.0
    f["f3"] = clamp((df["macd_main"] - df["macd_signal"]) / atr, -3.0, 3.0) / 3.0
    f["f4"] = clamp(pct_b, 0.0, 1.0) * 2.0 - 1.0
    f["f5"] = clamp((atr / close) * 1000.0, 0.0, 5.0) / 5.0
    f["f6"] = df["stoch_main"] / 100.0
    f["f7"] = df["stoch_signal"] / 100.0
    f["f8"] = clamp((close - df["close_lag10"]) / atr, -5.0, 5.0) / 5.0
    f["f9"] = clamp((df["ema_fast"] - df["ema_slow"]) / atr, -5.0, 5.0) / 5.0
    f["f10"] = clamp((close - df["ema200"]) / atr, -8.0, 8.0) / 8.0
    f["f11"] = clamp(df["tick_volume"] / df["vol_avg20"], 0.0, 3.0) / 3.0

    return f


def build_labels(df, horizon, label_threshold):
    close = df["close"]
    atr = df["atr"]
    future_close = close.shift(-horizon)
    future_move = (future_close - close) / atr

    label = pd.Series(np.nan, index=df.index)
    label[future_move > label_threshold] = 1.0
    label[future_move < -label_threshold] = 0.0
    return label


def export_mql5_header(clf, path):
    coefs = clf.coefs_
    intercepts = clf.intercepts_
    if len(coefs) != 3:
        raise ValueError(
            f"Expected a 3-weight-matrix MLP (12->{HIDDEN1}->{HIDDEN2}->1), "
            f"got {len(coefs)} layers. Did you change hidden_layer_sizes? "
            f"If so you must also resize the arrays in MLModel.mqh."
        )

    def fmt_matrix(mat):
        rows = []
        for row in mat:
            rows.append("   {" + ",".join(f"{v:.8f}" for v in row) + "}")
        return "{\n" + ",\n".join(rows) + "\n}"

    def fmt_vector(vec):
        return "{" + ",".join(f"{v:.8f}" for v in vec) + "}"

    w1, w2, w3 = coefs
    b1, b2, b3 = intercepts

    content = f"""//+------------------------------------------------------------------+
//| MLModel.mqh - AUTO-GENERATED by python/train_model.py             |
//| Trained weights for the 12 -> {HIDDEN1} -> {HIDDEN2} -> 1 MLP.                        |
//| Replace MQL5/Include/XAUUSD_ML_Scalper/MLModel.mqh with this file  |
//| (keep the filename MLModel.mqh) after reviewing the validation     |
//| metrics printed by train_model.py.                                 |
//+------------------------------------------------------------------+
#ifndef XAUUSD_ML_SCALPER_MLMODEL_MQH
#define XAUUSD_ML_SCALPER_MLMODEL_MQH
#property strict

#define ML_INPUT_SIZE {N_FEATURES}
#define ML_H1_SIZE     {HIDDEN1}
#define ML_H2_SIZE     {HIDDEN2}

double g_ML_W1[ML_INPUT_SIZE][ML_H1_SIZE] = {fmt_matrix(w1)};
double g_ML_B1[ML_H1_SIZE] = {fmt_vector(b1)};

double g_ML_W2[ML_H1_SIZE][ML_H2_SIZE] = {fmt_matrix(w2)};
double g_ML_B2[ML_H2_SIZE] = {fmt_vector(b2)};

double g_ML_W3[ML_H2_SIZE][1] = {fmt_matrix(w3)};
double g_ML_B3[1] = {fmt_vector(b3)};

double MLPredict(const double &features[])
{{
   if(ArraySize(features) != ML_INPUT_SIZE)
      return 0.5;

   double h1[ML_H1_SIZE];
   for(int j = 0; j < ML_H1_SIZE; j++)
   {{
      double sum = g_ML_B1[j];
      for(int i = 0; i < ML_INPUT_SIZE; i++)
         sum += features[i] * g_ML_W1[i][j];
      h1[j] = MathTanh(sum);
   }}

   double h2[ML_H2_SIZE];
   for(int j = 0; j < ML_H2_SIZE; j++)
   {{
      double sum = g_ML_B2[j];
      for(int i = 0; i < ML_H1_SIZE; i++)
         sum += h1[i] * g_ML_W2[i][j];
      h2[j] = MathTanh(sum);
   }}

   double outSum = g_ML_B3[0];
   for(int i = 0; i < ML_H2_SIZE; i++)
      outSum += h2[i] * g_ML_W3[i][0];

   return 1.0 / (1.0 + MathExp(-outSum));
}}

#endif // XAUUSD_ML_SCALPER_MLMODEL_MQH
"""
    with open(path, "w") as fh:
        fh.write(content)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--csv", required=True, help="CSV from ExportHistoryCSV.mq5")
    ap.add_argument("--horizon", type=int, default=5, help="bars ahead for the label")
    ap.add_argument("--label-threshold", type=float, default=0.5,
                     help="future move, in ATR units, required to label up/down")
    ap.add_argument("--test-size", type=float, default=0.2)
    ap.add_argument("--max-iter", type=int, default=500)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--output", default="MLModel_trained.mqh")
    args = ap.parse_args()

    df = pd.read_csv(args.csv, parse_dates=["time"])
    df = df.sort_values("time").reset_index(drop=True)
    if len(df) < 300:
        print(f"WARNING: only {len(df)} bars loaded. This is far too little data "
              f"for a real model - treat any result as a pipeline smoke test only.",
              file=sys.stderr)

    df = compute_indicators(df)
    features = build_features(df)
    labels = build_labels(df, args.horizon, args.label_threshold)

    data = pd.concat([features, labels.rename("label")], axis=1).dropna()
    if len(data) < 100:
        print("ERROR: not enough labeled rows after indicator warm-up / label "
              "filtering. Export more history.", file=sys.stderr)
        sys.exit(1)

    X = data[[f"f{i}" for i in range(N_FEATURES)]].values
    y = data["label"].values

    split = int(len(data) * (1.0 - args.test_size))
    X_train, X_test = X[:split], X[split:]
    y_train, y_test = y[:split], y[split:]

    print(f"Rows total={len(data)} train={len(X_train)} test={len(X_test)}")
    print(f"Label balance (train): up={y_train.mean():.3f}  (test): up={y_test.mean():.3f}")

    clf = MLPClassifier(
        hidden_layer_sizes=(HIDDEN1, HIDDEN2),
        activation="tanh",
        solver="adam",
        alpha=1e-3,
        max_iter=args.max_iter,
        random_state=args.seed,
        early_stopping=True,
        n_iter_no_change=15,
    )
    clf.fit(X_train, y_train)

    pred = clf.predict(X_test)
    acc = accuracy_score(y_test, pred)
    prec = precision_score(y_test, pred, zero_division=0)
    rec = recall_score(y_test, pred, zero_division=0)
    cm = confusion_matrix(y_test, pred)

    print("\n=== Out-of-sample validation (chronological holdout, never seen in training) ===")
    print(f"Accuracy:  {acc:.4f}")
    print(f"Precision: {prec:.4f}")
    print(f"Recall:    {rec:.4f}")
    print(f"Confusion matrix [[TN,FP],[FN,TP]]:\n{cm}")
    baseline = max(y_test.mean(), 1 - y_test.mean())
    print(f"Naive baseline (always predict majority class): {baseline:.4f}")
    if acc <= baseline + 0.02:
        print("\nWARNING: model is not meaningfully beating the naive majority-class "
              "baseline on held-out data. Do NOT deploy this model live - it has no "
              "demonstrated edge. This is common and expected on short/synthetic "
              "datasets; more/better data and feature work is needed.")

    export_mql5_header(clf, args.output)
    print(f"\nWrote {args.output}. Review the metrics above, then copy this file over "
          f"MQL5/Include/XAUUSD_ML_Scalper/MLModel.mqh to use it in the EA.")


if __name__ == "__main__":
    main()
