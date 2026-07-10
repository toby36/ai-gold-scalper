# XAUUSD ML Scalper (MQL5)

A self-contained MQL5 Expert Advisor for **XAUUSD (Gold) 1-minute scalping**,
sized around a **100 EUR account at 1:500 leverage**, with a small neural
network for signal generation and a hard-capped risk-management layer.

## Read this first — risk disclosure

High leverage does not create an edge. It only lets a small account open a
position that is large relative to its balance, which means **losses and
margin calls happen exactly as fast as gains**. No indicator, EA, or ML
model in existence can guarantee profit or safely "pump up" an account.
Gold M1 scalping is one of the hardest styles to trade because spread and
slippage are large relative to the average 1-minute move.

This EA is engineered to *survive* on a small account (hard daily-loss and
drawdown kill switches, capped risk per trade, no martingale/grid), not to
promise growth. Used carelessly — e.g. raising `InpMaxRiskPercentCap` far
above default, or trading the untrained placeholder model — it can and
will lose the account. **Demo-test for weeks before any live use. This is
not financial advice.**

## Why the bundled model ships untrained

`MLModel.mqh` ships with all weights set to `0`. That makes `MLPredict()`
always return exactly `0.5` (no signal), so the EA **will not open a
single trade** until you train a real model on real history and replace
that file. This is intentional — it stops the EA from silently trading on
random noise if someone forgets to train it.

## Architecture

```
Experts/XAUUSD_ML_Scalper/XAUUSD_ML_Scalper.mq5   <- the EA itself
Include/XAUUSD_ML_Scalper/
  MLModel.mqh        <- 12->8->4->1 MLP forward pass + weights
  FeatureEngine.mqh  <- builds the 12-feature vector each closed M1 bar
  RiskManager.mqh    <- position sizing, daily loss limit, drawdown kill switch
  Common.mqh         <- shared helpers
Scripts/XAUUSD_ML_Scalper/ExportHistoryCSV.mq5     <- exports broker M1 history to CSV
python/
  train_model.py         <- trains the MLP on the exported CSV, exports MQL5 weights
  backtest_simulator.py  <- Python re-implementation of the EA's risk/signal logic
  download_from_mt5.py   <- pulls bars/ticks straight from a running MT5 terminal
  requirements.txt
  requirements-mt5.txt   <- optional, Windows-only, for download_from_mt5.py
```

`Experts/`, `Include/` and `Scripts/` mirror the layout MetaTrader expects
under its own `MQL5/` data folder (this repo's `.gitignore` blanket-excludes
a top-level `MQL5/` directory and `*.mq5`/`*.mqh` files project-wide, so
these particular files were added with `git add -f` — worth knowing if you
add more files here later, they won't be picked up automatically).

The EA is fully self-contained: no external Python server, no WebRequest,
no DLL imports. The ML model is a small feed-forward network evaluated
natively in MQL5 on every closed M1 bar.

### Signal

12 features (RSI, MACD, Bollinger %B, ATR-normalised volatility, Stochastic,
10-bar momentum, EMA(8)/EMA(21) trend, price vs EMA(200), volume surge — see
`FeatureEngine.mqh` for exact formulas) feed a `12 → 8 → 4 → 1` MLP
(tanh hidden layers, sigmoid output) that outputs P(price up over the next
`horizon` bars). A trade only fires if confidence (`|P-0.5|*2`) clears
`InpConfidenceThreshold` (default `0.65`).

### Risk management (`RiskManager.mqh`)

- **Per-trade risk %** of current balance, hard-capped by `InpMaxRiskPercentCap`.
- **Position sizing** from the ATR-based stop-loss distance and the
  symbol's actual tick value — not a fixed lot size.
- **Free-margin usage cap** — a single position can never eat more than
  `InpMaxFreeMarginUsage`% of free margin, regardless of what the risk-%
  math says.
- **Daily loss limit** (`InpMaxDailyLossPercent`) — no new trades for the
  rest of the broker day once hit.
- **Drawdown kill switch** (`InpMaxDrawdownPercent`) — closes every
  position and halts the EA once equity falls that far from its peak.
- **No martingale, no grid, no "double down after a loss."** Deliberately
  absent — that class of logic is what typically blows small high-leverage
  accounts.

## Setup

1. Copy the `Experts/`, `Include/` and `Scripts/` folders from this
   directory into your terminal's `MQL5/` data folder, merging with the
   existing subfolders there (MetaTrader: **File → Open Data Folder**, then
   go into `MQL5/`).
2. Open `XAUUSD_ML_Scalper.mq5` in MetaEditor and compile (F7).
3. Attach to an **XAUUSD, M1** chart. Enable **AutoTrading**.
4. With the placeholder weights, it will run but never trade — see
   "Training" below before expecting any signals.

## Getting history data

Two ways to get a CSV in the `time,open,high,low,close,tick_volume` format
`train_model.py` expects — use whichever is more convenient:

**Option A — MQL5 script (works from inside MetaTrader, any OS the terminal runs on).**
Attach the `ExportHistoryCSV` script (in `Scripts/XAUUSD_ML_Scalper/`, once
copied into your `MQL5/Scripts/` folder) to an XAUUSD chart. It writes
`MQL5/Files/<SYMBOL>_M1_history.csv` in your terminal's data folder.

**Option B — `python/download_from_mt5.py` (no manual chart step, can also pull raw ticks).**
Uses the official `MetaTrader5` Python package to pull history straight from
a running, logged-in MT5 terminal. This only works run from the *same
machine* as that terminal (Windows, or MT5-under-Wine) — the package is a
thin wrapper around the terminal process itself, it does not talk to a
broker on its own, and it will not install on Linux/macOS (so it can't run
in this repo's dev sandbox either — see "What was actually validated" below).
```
pip install -r python/requirements-mt5.txt

# last 90 days of M1 bars
python python/download_from_mt5.py --symbol XAUUSD --timeframe M1 --days 90 \
    --out XAUUSD_M1_history.csv

# raw tick data for a date range (most brokers retain far less tick history than bar history)
python python/download_from_mt5.py --symbol XAUUSD --mode ticks \
    --from 2026-06-01 --to 2026-07-01 --out XAUUSD_ticks.csv
```
If the terminal isn't already logged in, pass `--login`, `--password` and
`--server`; otherwise just log in by hand first and omit those flags.
`train_model.py` currently consumes bar data — the tick CSV is there for
your own analysis/backtesting, not (yet) as direct training input.

Use as much M1 history as your broker actually retains — a few weeks is a
bare minimum smoke test, several months to a year is what you need for a
real attempt.

## Training on real history

1. Get a history CSV via Option A or B above.
2. `pip install -r python/requirements.txt`
3. `python python/train_model.py --csv <SYMBOL>_M1_history.csv --output MLModel_trained.mqh`
4. Read the printed out-of-sample accuracy/precision/recall **before doing
   anything else.** If accuracy is not meaningfully above the printed
   "naive baseline," the model has no demonstrated edge — do not deploy it.
5. If (and only if) the metrics look reasonable, copy `MLModel_trained.mqh`
   over `Include/XAUUSD_ML_Scalper/MLModel.mqh` (in your `MQL5/Include/`
   folder) and recompile the EA.
6. Re-train periodically — market regimes shift and a model trained on old
   data decays.

`FeatureEngine.mqh` and `train_model.py` compute indicators identically
(RSI/ATR via Wilder smoothing, Bollinger with population std, etc. — matching
MT5's own indicator conventions) so the live EA sees data statistically
consistent with what the model was trained on. If you modify one side,
mirror the change on the other and retrain.

## Backtesting

- **MetaTrader Strategy Tester** is the authoritative test — use it with
  real tick data for your broker/symbol before considering live use. This
  repo does not include a Strategy Tester run because the development
  environment here has no MetaTrader installation (Linux sandbox, no
  Windows/Wine, no MT5 terminal) — you must run this step yourself.
- `python/backtest_simulator.py` is a secondary, bar-based Python
  re-implementation of the exact same signal/risk logic (position sizing,
  ATR SL/TP, daily loss limit, drawdown kill switch). It approximates fills
  from bar high/low with a constant spread cost — useful for quickly
  sanity-checking risk behaviour and iterating on parameters, **not** a
  substitute for the real Strategy Tester.
  ```
  python python/backtest_simulator.py --csv <SYMBOL>_M1_history.csv \
      --balance 100 --leverage 500
  ```

### What was actually validated in this repo

The sandbox this EA was built in has no outbound access to market-data
providers (confirmed: connections to Yahoo Finance and similar hosts are
blocked by the environment's egress policy) and no MetaTrader installation,
so **no real XAUUSD data was available here**. What was verified instead:

- `train_model.py` and `backtest_simulator.py` were run end-to-end against
  60,000 bars of synthetic, regime-switching M1 data to confirm the full
  pipeline works mechanically: feature engineering → MLP training →
  out-of-sample evaluation → MQL5 weight export → the exported `.mqh`
  compiles to the same interface as the hand-written one.
- `backtest_simulator.py` on that synthetic data confirmed the risk logic
  behaves as designed on a 100/1:500 account: per-trade losses tracked the
  configured risk %, and the run **halted itself at the configured 20%
  drawdown cap** instead of continuing to bleed the account — i.e. the
  kill switch works.
- `download_from_mt5.py` was syntax-checked and its CLI/argument handling
  was exercised, but it could **not** be run end-to-end here: the
  `MetaTrader5` package only ships Windows wheels and there is no MT5
  terminal in this Linux sandbox to connect to. Run it yourself on the
  machine where your terminal lives, and sanity-check the first CSV it
  produces before trusting it for training.
- The MQL5 source was reviewed manually for correctness (indicator buffer
  indices, array bounds, order-fill handling) but **was not compiled by an
  actual MetaEditor/MT5 instance**, since none is available in this
  environment. Compile it yourself (step 2 above) and check the "Experts"
  log before attaching it to a live or even demo chart.
- None of the above is evidence of a trading edge on real gold prices —
  the synthetic data has injected autocorrelation the model can partially
  learn, which is expected and only proves the pipeline runs, not that it
  will predict real XAUUSD. You must retrain and backtest on real broker
  history (steps above) and demo-test before ever considering live use.

## Key inputs

| Input | Default | Purpose |
|---|---|---|
| `InpConfidenceThreshold` | 0.65 | Min model confidence to trade |
| `InpRiskPercentPerTrade` | 1.5 | % balance risked per trade |
| `InpMaxRiskPercentCap` | 3.0 | Absolute ceiling, even in aggressive mode |
| `InpMaxDailyLossPercent` | 6.0 | Stop trading for the day |
| `InpMaxDrawdownPercent` | 20.0 | Kill switch: close all, halt EA |
| `InpMaxFreeMarginUsage` | 40.0 | Max % of free margin per position |
| `InpAggressiveCompounding` | false | Scale risk % with equity growth, still capped |
| `InpATR_SL_Multiplier` / `InpATR_TP_Multiplier` | 1.5 / 2.5 | Stop/target distance in ATR |
| `InpMaxSpreadPoints` | 350 | Skip trading if spread too wide |
| `InpUseSessionFilter` | true | Restrict to configured server-time hours |

## License

No license file is included; all rights reserved by default. Add a license
of your choice if you intend to share this outside your own use.
