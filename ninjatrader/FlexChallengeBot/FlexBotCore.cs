// =====================================================================================
//  FlexChallengeBot - core components (platform independent, pure C#)
//
//  Everything in this file is free of NinjaTrader dependencies so that the risk engine,
//  the challenge state machine, the meta-labelling model and the intraday statistics
//  can be unit tested outside of the platform. The strategy (FlexChallengeBot.cs) wires
//  these components to NinjaTrader's order and data events.
//
//  Install: copy this file and FlexChallengeBot.cs into
//  Documents\NinjaTrader 8\bin\Custom\Strategies\ and compile (F5 in the NinjaScript editor).
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.Strategies.FlexBot
{
    public enum BalanceSourceMode
    {
        StrategyInternal,
        BrokerAccount
    }

    public enum SignalModule
    {
        OpeningRangeBreakout = 0,
        NoiseAreaMomentum = 1
    }

    public enum HaltReason
    {
        None,
        DailyLossLimit,
        DailyProfitCap,
        MaxTradesPerDay,
        ConsecutiveLosses,
        FloorProximity,
        ChallengePassed,
        ChallengeFailed,
        DataNotReady
    }

    // ---------------------------------------------------------------------------------
    //  Challenge rules (FundedNext Futures Flex 50K defaults)
    // ---------------------------------------------------------------------------------
    public sealed class ChallengeRules
    {
        public double StartingBalance = 50000.0;
        public double ProfitTarget = 2500.0;
        public double MaxLossLimit = 1500.0;        // End-of-day trailing
        public double LockOffset = 100.0;           // Threshold stops trailing at StartingBalance + LockOffset
        public double ConsistencyFraction = 0.40;   // Best day must stay <= 40% of the profit target
        public int MinTradingDays = 1;
        public bool StopAtProfitTarget = true;      // false = funded-account mode (no target halt, no consistency)

        public double LockedThreshold
        {
            get { return StartingBalance + LockOffset; }
        }

        public double ConsistencyDayCap
        {
            get { return ConsistencyFraction > 0 ? ProfitTarget * ConsistencyFraction : double.MaxValue; }
        }
    }

    // ---------------------------------------------------------------------------------
    //  Challenge account state machine: EOD trailing threshold, consistency-adjusted
    //  target, pass / fail detection and per-day bookkeeping.
    // ---------------------------------------------------------------------------------
    public sealed class ChallengeAccount
    {
        private readonly ChallengeRules rules;

        public ChallengeAccount(ChallengeRules rules)
        {
            if (rules == null) throw new ArgumentNullException("rules");
            this.rules = rules;
            Reset(rules.StartingBalance);
        }

        public ChallengeRules Rules { get { return rules; } }
        public double Balance { get; private set; }
        public double HighestEodBalance { get; private set; }
        public double DayStartBalance { get; private set; }
        public double BestDayProfit { get; private set; }
        public int TradingDays { get; private set; }
        public int CalendarDays { get; private set; }
        public DateTime CurrentDay { get; private set; }
        public int TradesToday { get; private set; }
        public int ConsecutiveLossesToday { get; private set; }
        public bool Passed { get; private set; }
        public bool Failed { get; private set; }
        public HaltReason DayHalt { get; set; }

        public double Threshold
        {
            get { return Math.Min(HighestEodBalance - rules.MaxLossLimit, rules.LockedThreshold); }
        }

        public double TotalProfit
        {
            get { return Balance - rules.StartingBalance; }
        }

        public double DayRealized
        {
            get { return Balance - DayStartBalance; }
        }

        public bool IsFinished
        {
            get { return Passed || Failed; }
        }

        /// <summary>Profit target after the consistency rule adjusts it for the best day so far
        /// (including the day in progress with the given intraday profit).</summary>
        public double EffectiveTarget(double currentDayProfit)
        {
            if (!rules.StopAtProfitTarget) return double.MaxValue;
            double best = Math.Max(BestDayProfit, currentDayProfit);
            if (rules.ConsistencyFraction <= 0) return rules.ProfitTarget;
            return Math.Max(rules.ProfitTarget, best / rules.ConsistencyFraction);
        }

        public void Reset(double balance)
        {
            Balance = balance;
            HighestEodBalance = Math.Max(balance, rules.StartingBalance);
            DayStartBalance = balance;
            BestDayProfit = 0;
            TradingDays = 0;
            CalendarDays = 0;
            CurrentDay = DateTime.MinValue;
            TradesToday = 0;
            ConsecutiveLossesToday = 0;
            Passed = false;
            Failed = false;
            DayHalt = HaltReason.None;
        }

        /// <summary>Restores a previously persisted live state.</summary>
        public void Restore(ChallengeSnapshot s)
        {
            Balance = s.Balance;
            HighestEodBalance = Math.Max(s.HighestEodBalance, rules.StartingBalance);
            DayStartBalance = s.DayStartBalance;
            BestDayProfit = s.BestDayProfit;
            TradingDays = s.TradingDays;
            CalendarDays = s.CalendarDays;
            CurrentDay = s.CurrentDay;
            TradesToday = s.TradesToday;
            ConsecutiveLossesToday = s.ConsecutiveLossesToday;
            Passed = s.Passed;
            Failed = s.Failed;
            DayHalt = s.DayHalt;
        }

        public ChallengeSnapshot Snapshot()
        {
            return new ChallengeSnapshot
            {
                Balance = Balance,
                HighestEodBalance = HighestEodBalance,
                DayStartBalance = DayStartBalance,
                BestDayProfit = BestDayProfit,
                TradingDays = TradingDays,
                CalendarDays = CalendarDays,
                CurrentDay = CurrentDay,
                TradesToday = TradesToday,
                ConsecutiveLossesToday = ConsecutiveLossesToday,
                Passed = Passed,
                Failed = Failed,
                DayHalt = DayHalt
            };
        }

        /// <summary>Closes the current trading day (EOD balance update) and opens the next one.</summary>
        public void StartNewDay(DateTime tradingDay)
        {
            if (CurrentDay != DateTime.MinValue && tradingDay.Date <= CurrentDay.Date) return;

            if (CurrentDay != DateTime.MinValue)
                CloseDay();

            CurrentDay = tradingDay.Date;
            DayStartBalance = Balance;
            TradesToday = 0;
            ConsecutiveLossesToday = 0;
            if (DayHalt != HaltReason.ChallengePassed && DayHalt != HaltReason.ChallengeFailed)
                DayHalt = HaltReason.None;
        }

        private void CloseDay()
        {
            CalendarDays++;
            double dayProfit = Balance - DayStartBalance;
            if (TradesToday > 0) TradingDays++;
            if (dayProfit > BestDayProfit) BestDayProfit = dayProfit;
            if (Balance > HighestEodBalance) HighestEodBalance = Balance;
            EvaluatePass();
        }

        /// <summary>Applies values copied from the prop-firm dashboard. Non-positive values are ignored.</summary>
        public void ApplyManualOverrides(double balance, double highestEodBalance, double bestDayProfit, int tradingDays)
        {
            if (balance > 0)
            {
                double delta = balance - Balance;
                Balance = balance;
                DayStartBalance += delta;
            }
            if (highestEodBalance > 0) HighestEodBalance = Math.Max(highestEodBalance, rules.StartingBalance);
            if (bestDayProfit > 0) BestDayProfit = bestDayProfit;
            if (tradingDays > 0) TradingDays = tradingDays;
            EvaluateFailure(Balance);
        }

        /// <summary>Clears a day-level halt that was persisted (used when the user restarts with new limits).</summary>
        public void ClearDayHalt()
        {
            if (DayHalt != HaltReason.ChallengePassed && DayHalt != HaltReason.ChallengeFailed)
                DayHalt = HaltReason.None;
        }

        /// <summary>Balance synchronisation from the broker (only call while flat).</summary>
        public void SetBalance(double balance)
        {
            Balance = balance;
            EvaluateFailure(balance);
        }

        public void RegisterTradeOpened()
        {
            TradesToday++;
        }

        public void RegisterTradeClosed(double netPnl)
        {
            Balance += netPnl;
            if (netPnl < 0) ConsecutiveLossesToday++;
            else ConsecutiveLossesToday = 0;
            EvaluateFailure(Balance);
            EvaluatePass();
        }

        /// <summary>Intraday breach check against the trailing threshold (equity incl. open P/L).</summary>
        public bool CheckEquityBreach(double equity)
        {
            return EvaluateFailure(equity);
        }

        private bool EvaluateFailure(double equity)
        {
            if (Failed) return true;
            if (equity <= Threshold)
            {
                Failed = true;
                DayHalt = HaltReason.ChallengeFailed;
                return true;
            }
            return false;
        }

        private void EvaluatePass()
        {
            if (Passed || Failed || !rules.StopAtProfitTarget) return;
            double dayProfit = Balance - DayStartBalance;
            int daysIncludingToday = TradingDays + (TradesToday > 0 ? 1 : 0);
            if (TotalProfit >= EffectiveTarget(dayProfit) && daysIncludingToday >= rules.MinTradingDays)
            {
                Passed = true;
                DayHalt = HaltReason.ChallengePassed;
            }
        }
    }

    public sealed class ChallengeSnapshot
    {
        public double Balance;
        public double HighestEodBalance;
        public double DayStartBalance;
        public double BestDayProfit;
        public int TradingDays;
        public int CalendarDays;
        public DateTime CurrentDay;
        public int TradesToday;
        public int ConsecutiveLossesToday;
        public bool Passed;
        public bool Failed;
        public HaltReason DayHalt;
    }

    // ---------------------------------------------------------------------------------
    //  Persistent live state (key=value text, invariant culture, atomic replace)
    // ---------------------------------------------------------------------------------
    public static class StateStore
    {
        public static void Save(string path, ChallengeSnapshot s)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            CultureInfo ci = CultureInfo.InvariantCulture;
            sb.AppendLine("Version=1");
            sb.AppendLine("Balance=" + s.Balance.ToString("R", ci));
            sb.AppendLine("HighestEodBalance=" + s.HighestEodBalance.ToString("R", ci));
            sb.AppendLine("DayStartBalance=" + s.DayStartBalance.ToString("R", ci));
            sb.AppendLine("BestDayProfit=" + s.BestDayProfit.ToString("R", ci));
            sb.AppendLine("TradingDays=" + s.TradingDays.ToString(ci));
            sb.AppendLine("CalendarDays=" + s.CalendarDays.ToString(ci));
            sb.AppendLine("CurrentDay=" + s.CurrentDay.ToString("yyyy-MM-dd", ci));
            sb.AppendLine("TradesToday=" + s.TradesToday.ToString(ci));
            sb.AppendLine("ConsecutiveLossesToday=" + s.ConsecutiveLossesToday.ToString(ci));
            sb.AppendLine("Passed=" + (s.Passed ? "1" : "0"));
            sb.AppendLine("Failed=" + (s.Failed ? "1" : "0"));
            sb.AppendLine("DayHalt=" + s.DayHalt.ToString());

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static ChallengeSnapshot Load(string path)
        {
            if (!File.Exists(path)) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            CultureInfo ci = CultureInfo.InvariantCulture;
            var s = new ChallengeSnapshot();
            s.Balance = ReadDouble(map, "Balance", ci);
            s.HighestEodBalance = ReadDouble(map, "HighestEodBalance", ci);
            s.DayStartBalance = ReadDouble(map, "DayStartBalance", ci);
            s.BestDayProfit = ReadDouble(map, "BestDayProfit", ci);
            s.TradingDays = (int)ReadDouble(map, "TradingDays", ci);
            s.CalendarDays = (int)ReadDouble(map, "CalendarDays", ci);
            string day;
            DateTime parsed;
            s.CurrentDay = map.TryGetValue("CurrentDay", out day) &&
                           DateTime.TryParseExact(day, "yyyy-MM-dd", ci, DateTimeStyles.None, out parsed)
                ? parsed
                : DateTime.MinValue;
            s.TradesToday = (int)ReadDouble(map, "TradesToday", ci);
            s.ConsecutiveLossesToday = (int)ReadDouble(map, "ConsecutiveLossesToday", ci);
            string flag;
            s.Passed = map.TryGetValue("Passed", out flag) && flag == "1";
            s.Failed = map.TryGetValue("Failed", out flag) && flag == "1";
            HaltReason halt;
            s.DayHalt = map.TryGetValue("DayHalt", out flag) && Enum.TryParse(flag, out halt) ? halt : HaltReason.None;

            if (s.Balance <= 0 || s.HighestEodBalance <= 0)
                throw new InvalidDataException("State file '" + path + "' is corrupt (non-positive balance).");
            return s;
        }

        private static double ReadDouble(Dictionary<string, string> map, string key, CultureInfo ci)
        {
            string v;
            double d;
            if (map.TryGetValue(key, out v) && double.TryParse(v, NumberStyles.Float, ci, out d)) return d;
            return 0;
        }
    }

    // ---------------------------------------------------------------------------------
    //  Position sizing
    // ---------------------------------------------------------------------------------
    public static class RiskSizer
    {
        /// <summary>Largest contract count whose worst-case loss (stop + slippage + fees) fits the budget.</summary>
        public static int ContractsForRisk(double riskBudget, double stopTicks, double tickValue,
                                           double slippageTicks, double roundTurnCost, int maxContracts)
        {
            if (riskBudget <= 0 || stopTicks <= 0 || tickValue <= 0 || maxContracts <= 0) return 0;
            double lossPerContract = (stopTicks + Math.Max(0, slippageTicks)) * tickValue + Math.Max(0, roundTurnCost);
            if (lossPerContract <= 0) return 0;
            int n = (int)Math.Floor(riskBudget / lossPerContract + 1e-9);
            return Math.Max(0, Math.Min(n, maxContracts));
        }

        /// <summary>Largest contract count whose 1R gain still fits the profit room (consistency cap / target).</summary>
        public static int ContractsForProfitRoom(double profitRoom, double stopTicks, double tickValue, int maxContracts)
        {
            if (profitRoom == double.MaxValue) return maxContracts;
            if (profitRoom <= 0 || stopTicks <= 0 || tickValue <= 0) return 0;
            int n = (int)Math.Floor(profitRoom / (stopTicks * tickValue) + 1e-9);
            return Math.Max(0, Math.Min(n, maxContracts));
        }
    }

    // ---------------------------------------------------------------------------------
    //  Wilder ATR on RTH daily bars (computed from intraday data, no extra series needed)
    // ---------------------------------------------------------------------------------
    public sealed class WilderAtr
    {
        private readonly int period;
        private int count;
        private double sum;

        public WilderAtr(int period)
        {
            if (period < 1) throw new ArgumentOutOfRangeException("period");
            this.period = period;
        }

        public double Value { get; private set; }

        public int Count
        {
            get { return count; }
        }

        public bool IsReady
        {
            get { return count >= period; }
        }

        public void Update(double high, double low, double prevClose)
        {
            double tr = high - low;
            if (!double.IsNaN(prevClose) && prevClose > 0)
                tr = Math.Max(tr, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            count++;
            if (count <= period)
            {
                sum += tr;
                Value = sum / count;
            }
            else
            {
                Value = (Value * (period - 1) + tr) / period;
            }
        }
    }

    // ---------------------------------------------------------------------------------
    //  Rolling per-minute-of-session profile over the last N completed sessions.
    //  Used for (a) the noise area (avg |close/open - 1| per minute) and
    //  (b) relative cumulative volume per minute.
    // ---------------------------------------------------------------------------------
    public sealed class MinuteProfileHistory
    {
        private readonly int lookbackDays;
        private readonly int slots;
        private readonly Queue<double[]> days = new Queue<double[]>();
        private readonly double[] sum;
        private readonly int[] cnt;
        private double[] current;

        public MinuteProfileHistory(int lookbackDays, int slots)
        {
            if (lookbackDays < 1) throw new ArgumentOutOfRangeException("lookbackDays");
            if (slots < 1) throw new ArgumentOutOfRangeException("slots");
            this.lookbackDays = lookbackDays;
            this.slots = slots;
            sum = new double[slots];
            cnt = new int[slots];
            BeginDay();
        }

        public int CompletedDays
        {
            get { return days.Count; }
        }

        public bool IsReady
        {
            get { return days.Count >= lookbackDays; }
        }

        public void BeginDay()
        {
            current = new double[slots];
            for (int i = 0; i < slots; i++) current[i] = double.NaN;
        }

        public void Record(int slot, double value)
        {
            if (slot < 0 || slot >= slots || double.IsNaN(value) || double.IsInfinity(value)) return;
            current[slot] = value;
        }

        /// <summary>Commits the current session into the rolling window. Sessions with fewer than
        /// minValidSlots recorded values (data gaps) are discarded.</summary>
        public void CommitDay(int minValidSlots)
        {
            int valid = 0;
            for (int i = 0; i < slots; i++) if (!double.IsNaN(current[i])) valid++;
            if (valid >= minValidSlots)
            {
                days.Enqueue(current);
                Accumulate(current, +1);
                if (days.Count > lookbackDays) Accumulate(days.Dequeue(), -1);
            }
            BeginDay();
        }

        private void Accumulate(double[] day, int sign)
        {
            for (int i = 0; i < slots; i++)
            {
                if (double.IsNaN(day[i])) continue;
                sum[i] += sign * day[i];
                cnt[i] += sign;
            }
        }

        /// <summary>Average value at the slot over the window; NaN if fewer than half the days have data.</summary>
        public double Mean(int slot)
        {
            if (slot < 0 || slot >= slots) return double.NaN;
            if (cnt[slot] < Math.Max(1, (lookbackDays + 1) / 2)) return double.NaN;
            return sum[slot] / cnt[slot];
        }
    }

    // ---------------------------------------------------------------------------------
    //  Online logistic regression with running standardisation and prequential
    //  (predict-then-train) evaluation. Used as a meta-labelling filter.
    // ---------------------------------------------------------------------------------
    public sealed class OnlineLogisticModel
    {
        private readonly int n;
        private readonly double[] w;
        private readonly double[] mean;
        private readonly double[] m2;
        private double bias;
        private long statCount;
        private readonly double learningRate;
        private readonly double l2;

        private double brierModelSum;
        private double brierBaseSum;
        private int evaluated;
        private int positives;

        public OnlineLogisticModel(int features, double learningRate, double l2)
        {
            if (features < 1) throw new ArgumentOutOfRangeException("features");
            n = features;
            w = new double[n];
            mean = new double[n];
            m2 = new double[n];
            this.learningRate = learningRate;
            this.l2 = l2;
        }

        public int Samples { get; private set; }
        public int FeatureCount { get { return n; } }

        public double BaseRate
        {
            get { return Samples > 0 ? (double)positives / Samples : 0.5; }
        }

        /// <summary>Out-of-sample Brier score of the model (each prediction made before training on that sample).</summary>
        public double PrequentialBrier
        {
            get { return evaluated > 0 ? brierModelSum / evaluated : double.NaN; }
        }

        /// <summary>Out-of-sample Brier score of the naive running base-rate forecaster.</summary>
        public double BaselineBrier
        {
            get { return evaluated > 0 ? brierBaseSum / evaluated : double.NaN; }
        }

        /// <summary>Brier skill score vs. base rate (positive = model adds information).</summary>
        public double Skill
        {
            get
            {
                if (evaluated == 0 || BaselineBrier <= 0) return 0;
                return 1.0 - PrequentialBrier / BaselineBrier;
            }
        }

        public double Predict(double[] x)
        {
            CheckLength(x);
            double z = bias;
            for (int i = 0; i < n; i++) z += w[i] * Standardise(x[i], i);
            return Sigmoid(z);
        }

        public void Train(double[] x, int label)
        {
            CheckLength(x);
            int y = label > 0 ? 1 : 0;

            double pModel = Predict(x);
            double pBase = Samples > 0 ? (double)positives / Samples : 0.5;
            brierModelSum += (pModel - y) * (pModel - y);
            brierBaseSum += (pBase - y) * (pBase - y);
            evaluated++;

            statCount++;
            for (int i = 0; i < n; i++)
            {
                double d = x[i] - mean[i];
                mean[i] += d / statCount;
                m2[i] += d * (x[i] - mean[i]);
            }

            double p = Predict(x);
            double g = p - y;
            double lr = learningRate / Math.Sqrt(1.0 + Samples / 50.0);
            for (int i = 0; i < n; i++)
                w[i] -= lr * (g * Standardise(x[i], i) + l2 * w[i]);
            bias -= lr * g;

            Samples++;
            positives += y;
        }

        private double Standardise(double v, int i)
        {
            if (statCount < 2) return 0;
            double var = m2[i] / (statCount - 1);
            double sd = var > 1e-12 ? Math.Sqrt(var) : 1.0;
            double z = (v - mean[i]) / sd;
            if (z > 5) z = 5;
            else if (z < -5) z = -5;
            return z;
        }

        private void CheckLength(double[] x)
        {
            if (x == null || x.Length != n)
                throw new ArgumentException("Feature vector must have " + n + " elements.");
        }

        private static double Sigmoid(double z)
        {
            if (z >= 0)
            {
                double e = Math.Exp(-z);
                return 1.0 / (1.0 + e);
            }
            double ez = Math.Exp(z);
            return ez / (1.0 + ez);
        }
    }

    // ---------------------------------------------------------------------------------
    //  Triple-barrier labelling of EVERY primary signal (taken or filtered out) so that
    //  the meta model never starves and is not trained on a selection-biased sample.
    // ---------------------------------------------------------------------------------
    public sealed class TripleBarrierLabeler
    {
        private sealed class Pending
        {
            public double[] Features;
            public int Direction;
            public double Entry;
            public double Upper;
            public double Lower;
            public DateTime Expiry;
        }

        private readonly List<Pending> pending = new List<Pending>();
        private readonly OnlineLogisticModel model;

        public TripleBarrierLabeler(OnlineLogisticModel model)
        {
            if (model == null) throw new ArgumentNullException("model");
            this.model = model;
        }

        public int PendingCount
        {
            get { return pending.Count; }
        }

        public void Add(double[] features, int direction, double entry, double riskPoints, double rewardR, DateTime expiry)
        {
            if (riskPoints <= 0 || direction == 0) return;
            var p = new Pending
            {
                Features = (double[])features.Clone(),
                Direction = direction,
                Entry = entry,
                Expiry = expiry
            };
            if (direction > 0)
            {
                p.Upper = entry + rewardR * riskPoints;
                p.Lower = entry - riskPoints;
            }
            else
            {
                p.Upper = entry + riskPoints;
                p.Lower = entry - rewardR * riskPoints;
            }
            pending.Add(p);
        }

        /// <summary>Updates every open label with a completed bar. When both barriers are touched
        /// inside the same bar the stop is assumed to have been hit first (conservative).</summary>
        public void OnBar(double high, double low, double close, DateTime time)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                int label = -1;
                if (p.Direction > 0)
                {
                    if (low <= p.Lower) label = 0;
                    else if (high >= p.Upper) label = 1;
                }
                else
                {
                    if (high >= p.Upper) label = 0;
                    else if (low <= p.Lower) label = 1;
                }

                if (label < 0 && time >= p.Expiry)
                    label = (close - p.Entry) * p.Direction > 0 ? 1 : 0;

                if (label >= 0)
                {
                    model.Train(p.Features, label);
                    pending.RemoveAt(i);
                }
            }
        }

        /// <summary>Resolves all open labels at the given price (end of session).</summary>
        public void ExpireAll(double close)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                model.Train(p.Features, (close - p.Entry) * p.Direction > 0 ? 1 : 0);
            }
            pending.Clear();
        }
    }

    // ---------------------------------------------------------------------------------
    //  Economic calendar blackout windows (Eastern Time)
    // ---------------------------------------------------------------------------------
    public sealed class NewsCalendar
    {
        private readonly List<TimeSpan> recurring = new List<TimeSpan>();
        private readonly List<DateTime> events = new List<DateTime>();

        public NewsCalendar(string recurringTimes, string datedEvents)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            foreach (string token in Split(recurringTimes))
            {
                DateTime t;
                if (DateTime.TryParseExact(token, "HH:mm", ci, DateTimeStyles.None, out t))
                    recurring.Add(t.TimeOfDay);
                else
                    throw new FormatException("Invalid recurring blackout time '" + token + "' (expected HH:mm).");
            }
            foreach (string token in Split(datedEvents))
            {
                DateTime t;
                if (DateTime.TryParseExact(token, "yyyy-MM-dd HH:mm", ci, DateTimeStyles.None, out t))
                    events.Add(t);
                else
                    throw new FormatException("Invalid event blackout '" + token + "' (expected yyyy-MM-dd HH:mm).");
            }
            events.Sort();
        }

        public int RecurringCount { get { return recurring.Count; } }
        public int EventCount { get { return events.Count; } }

        public bool IsBlackout(DateTime et, int minutesBefore, int minutesAfter)
        {
            foreach (TimeSpan ts in recurring)
                if (InWindow(et, et.Date + ts, minutesBefore, minutesAfter)) return true;
            foreach (DateTime ev in events)
                if (InWindow(et, ev, minutesBefore, minutesAfter)) return true;
            return false;
        }

        /// <summary>True if a dated (high impact) event starts within the next 'minutes'.</summary>
        public bool DatedEventWithin(DateTime et, int minutes)
        {
            foreach (DateTime ev in events)
            {
                double dt = (ev - et).TotalMinutes;
                if (dt >= 0 && dt <= minutes) return true;
            }
            return false;
        }

        private static bool InWindow(DateTime now, DateTime ev, int before, int after)
        {
            return now >= ev.AddMinutes(-before) && now <= ev.AddMinutes(after);
        }

        private static IEnumerable<string> Split(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (string part in s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }
    }

    // ---------------------------------------------------------------------------------
    //  CSV trade journal
    // ---------------------------------------------------------------------------------
    public sealed class TradeJournal
    {
        private readonly string path;
        private static readonly string Header =
            "ExitTimeET,Module,Direction,Contracts,EntryAvg,InitialStop,RiskPoints,NetPnL,RMultiple,MetaProb,ExitReason,Balance,Threshold,DayPnL,Features";

        public TradeJournal(string path)
        {
            this.path = path;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(path)) File.AppendAllText(path, Header + Environment.NewLine, Encoding.UTF8);
        }

        public string FilePath { get { return path; } }

        public void Write(DateTime exitEt, SignalModule module, int direction, int contracts, double entryAvg,
                          double initialStop, double riskPoints, double netPnl, double rMultiple, double metaProb,
                          string exitReason, double balance, double threshold, double dayPnl, double[] features)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(exitEt.ToString("yyyy-MM-dd HH:mm:ss", ci)).Append(',');
            sb.Append(module).Append(',');
            sb.Append(direction > 0 ? "Long" : "Short").Append(',');
            sb.Append(contracts.ToString(ci)).Append(',');
            sb.Append(entryAvg.ToString("0.#####", ci)).Append(',');
            sb.Append(initialStop.ToString("0.#####", ci)).Append(',');
            sb.Append(riskPoints.ToString("0.#####", ci)).Append(',');
            sb.Append(netPnl.ToString("0.00", ci)).Append(',');
            sb.Append(rMultiple.ToString("0.000", ci)).Append(',');
            sb.Append(double.IsNaN(metaProb) ? "" : metaProb.ToString("0.0000", ci)).Append(',');
            sb.Append((exitReason ?? "").Replace(',', ';')).Append(',');
            sb.Append(balance.ToString("0.00", ci)).Append(',');
            sb.Append(threshold.ToString("0.00", ci)).Append(',');
            sb.Append(dayPnl.ToString("0.00", ci)).Append(',');
            if (features != null)
            {
                for (int i = 0; i < features.Length; i++)
                {
                    if (i > 0) sb.Append('|');
                    sb.Append(features[i].ToString("0.######", ci));
                }
            }
            sb.Append(Environment.NewLine);
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }

    // ---------------------------------------------------------------------------------
    //  Back-test challenge statistics (Monte-Carlo-free, sequential attempts)
    // ---------------------------------------------------------------------------------
    public sealed class ChallengeAttemptStats
    {
        private readonly List<int> passDays = new List<int>();

        public int Attempts { get; private set; }
        public int Passed { get; private set; }
        public int Failed { get; private set; }

        public void RecordPass(int tradingDays)
        {
            Attempts++;
            Passed++;
            passDays.Add(tradingDays);
        }

        public void RecordFail()
        {
            Attempts++;
            Failed++;
        }

        public double PassRate
        {
            get { return Attempts > 0 ? (double)Passed / Attempts : 0; }
        }

        public double MedianDaysToPass
        {
            get
            {
                if (passDays.Count == 0) return double.NaN;
                var sorted = new List<int>(passDays);
                sorted.Sort();
                int m = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2.0;
            }
        }

        public double MeanDaysToPass
        {
            get
            {
                if (passDays.Count == 0) return double.NaN;
                double s = 0;
                foreach (int d in passDays) s += d;
                return s / passDays.Count;
            }
        }
    }
}
