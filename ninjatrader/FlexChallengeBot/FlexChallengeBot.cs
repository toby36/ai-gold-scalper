// =====================================================================================
//  FlexChallengeBot - NinjaTrader 8 intraday strategy for the FundedNext Futures Flex 50K
//
//  Signal engine
//    1. Opening Range Breakout (ORB) on the first N RTH minutes, volatility-normalised
//       width filter, VWAP side filter and relative-volume ("in play") filter.
//    2. Noise-Area Momentum: price leaves the average intraday move envelope computed
//       per minute-of-session over the last N sessions, evaluated on half-hour checkpoints,
//       exit when price falls back inside max(boundary, VWAP).
//    3. Meta-labelling filter: online logistic regression trained on triple-barrier labels
//       of every primary signal (taken or not), activated only when its out-of-sample
//       Brier skill against the base rate is positive.
//
//  Risk engine (prop-firm first)
//    - Models the EOD trailing max-loss threshold incl. the lock level, the 40% consistency
//      rule (consistency-adjusted effective target) and pass / fail.
//    - Tick-level equity guard: threshold buffer, self-imposed daily loss limit,
//      consistency-safe daily profit cap, target-reached flatten.
//    - Position size = min(risk budget, room to threshold, daily room, profit-cap room,
//      target room, contract limit), accounting for slippage and commissions.
//
//  Execution engine
//    - Unmanaged order handling, two independent OCO legs (scalp + runner) so a target
//      fill can never orphan or overfill the protective stop, partial-fill aware,
//      breakeven after the scalp target, ratcheting trailing stop, rejection handling,
//      overfill correction, cancel-then-flatten with watchdog, session-end and early-close
//      aware flattening, live state persisted to disk.
//
//  Requirements: primary series = 1 Minute bars of an equity index future (MNQ/MES/NQ/ES),
//  trading hours template "CME US Index Futures ETH", at least 40 days loaded.
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FlexBot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class FlexChallengeBot : Strategy
    {
        #region Constants and private types

        private const int SessionSlots = 390;          // 09:30 - 16:00 ET in minutes
        private const int FeatureCount = 13;
        private const int MinValidSlotsPerSession = 120;
        private const int EfficiencyLookback = 30;
        private const int MaxFlattenRetries = 3;
        private const double FlattenWatchdogSeconds = 2.0;
        private static readonly TimeSpan RthOpen = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan RthClose = new TimeSpan(16, 0, 0);
        private const int TradingDayRolloverHourEt = 18;  // CME Globex session start

        private static readonly HashSet<string> MicroRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MNQ", "MES", "MYM", "M2K", "MGC", "MCL", "SIL", "MBT", "MET", "M6E", "M6A", "M6B", "MJY", "MSF", "MCD", "MNG", "MHG"
        };

        private sealed class Signal
        {
            public SignalModule Module;
            public int Direction;
            public double ReferencePrice;
            public double StopPrice;
            public double RiskPoints;
            public string Description;
        }

        #endregion

        #region Private state

        // configuration
        private bool configurationValid;
        private string configurationError;
        private TimeZoneInfo easternTz;
        private ChallengeRules rules;
        private ChallengeAccount account;
        private ChallengeAttemptStats attemptStats;
        private OnlineLogisticModel metaModel;
        private TripleBarrierLabeler labeler;
        private MinuteProfileHistory noiseProfile;
        private MinuteProfileHistory volumeProfile;
        private WilderAtr dailyAtr;
        private NewsCalendar news;
        private TradeJournal journal;
        private ATR atr1m;
        private SessionIterator sessionIterator;
        private string stateFilePath;
        private bool liveStateActive;
        private bool summaryPrinted;
        private double tickValue;
        private int maxContractsEffective;

        // trading-day / session state
        private DateTime tradingDay = DateTime.MinValue;
        private DateTime sessionEndEt = DateTime.MinValue;
        private DateTime lastProcessedBarTime = DateTime.MinValue;
        private bool rthActive;
        private bool rthCommitted;
        private double dayOpen;
        private double rthHigh;
        private double rthLow;
        private double rthClose;
        private double prevRthClose = double.NaN;
        private double prevRthOpen = double.NaN;
        private double cumVolume;
        private double cumPv;
        private double vwap = double.NaN;
        private double orHigh;
        private double orLow;
        private double orOpen;
        private double orClose;
        private bool orComplete;
        private bool orbLongSignaled;
        private bool orbShortSignaled;
        private int orbEntriesToday;
        private int noiseEntriesToday;
        private double noiseUpper = double.NaN;
        private double noiseLower = double.NaN;
        private bool dataReadyAnnounced;

        // trade state
        private int tradeSeq;
        private bool tradeActive;
        private bool tradeIsVirtual;
        private int tradeDir;
        private SignalModule tradeModule;
        private double[] tradeFeatures;
        private double tradeProb = double.NaN;
        private int scalpPlannedQty;
        private int scalpOpen;
        private int runnerOpen;
        private int scalpAllocated;
        private int signedPos;
        private int entryFilled;
        private double entryAvg;
        private double initialStopPrice;
        private double currentStopPrice;
        private double actualRiskPoints;
        private double scalpTargetPrice;
        private double runnerTargetPrice;
        private double tradeCash;
        private double tradeCommission;
        private double tradeMfePoints;
        private DateTime tradeEntryEt;
        private bool scalpDone;
        private bool flattenRequested;
        private DateTime flattenRequestedUtc;
        private int flattenRetries;
        private string exitReason;
        private string flattenReasonText = "Flatten";
        private int ocoNonce;
        private bool liveStateBlocked;
        private Order neutraliseOrder;

        private string entryName;
        private string scalpStopName;
        private string scalpTargetName;
        private string runnerStopName;
        private string runnerTargetName;
        private string flattenName;
        private string fixName;

        private Order entryOrder;
        private Order scalpStopOrder;
        private Order scalpTargetOrder;
        private Order runnerStopOrder;
        private Order runnerTargetOrder;
        private Order exitOrder;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Prop-firm grade intraday ORB + noise-area momentum strategy with meta-labelling filter, "
                              + "built around the FundedNext Futures Flex 50K rules (EOD trailing max loss, 40% consistency).";
                Name = "FlexChallengeBot";
                Calculate = Calculate.OnPriceChange;
                IsUnmanaged = true;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 60;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 1;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = EfficiencyLookback + 2;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Challenge
                StartingBalance = 50000;
                ProfitTargetUsd = 2500;
                MaxLossLimitUsd = 1500;
                ThresholdLockOffsetUsd = 100;
                ConsistencyPercent = 40;
                MinTradingDays = 1;
                StopAtProfitTarget = true;
                BalanceSource = BalanceSourceMode.StrategyInternal;
                ManualCurrentBalance = 0;
                ManualHighestEodBalance = 0;
                ManualBestDayProfit = 0;
                ManualTradingDays = 0;
                ResetLiveState = false;

                // Risk
                RiskPerTradeUsd = 250;
                DailyLossLimitUsd = 450;
                DailyProfitCapUsd = 900;
                ThresholdBufferUsd = 150;
                ThresholdRiskFraction = 0.30;
                PassBufferUsd = 40;
                MaxTradesPerDay = 4;
                MaxConsecutiveLosses = 2;
                MaxContractsOverride = 0;
                CommissionPerContractRoundTurn = 1.04;
                SlippageTicksForSizing = 1;

                // Session
                LastEntryTimeEt = 1500;
                FlattenTimeEt = 1550;
                FlattenMinutesBeforeSessionEnd = 5;

                // ORB
                EnableOrb = true;
                OrMinutes = 5;
                OrbEntryEndEt = 1130;
                OrbMinWidthAtr = 0.04;
                OrbMaxWidthAtr = 0.40;
                OrbStopFraction = 1.0;
                OrbMaxExtensionFraction = 0.6;
                OrbRequireVwapSide = true;
                OrbMinRelativeVolume = 0.8;
                OrbMaxEntriesPerDay = 2;

                // Noise area
                EnableNoise = true;
                NoiseLookbackDays = 14;
                NoiseBandMultiplier = 1.0;
                NoiseCheckIntervalMinutes = 30;
                NoiseFirstCheckEt = 1000;
                NoiseStopBufferAtr = 1.5;
                NoiseMaxEntriesPerDay = 2;

                // Exits
                ScalpFraction = 0.5;
                ScalpTargetR = 1.0;
                RunnerTargetR = 3.0;
                BreakevenOffsetTicks = 2;
                TrailActivationR = 1.0;
                TrailAtrMultiplier = 3.0;
                VwapTrailOffsetTicks = 4;
                MinStopTicks = 16;
                MaxStopAtrFraction = 0.30;
                MaxHoldMinutes = 240;

                // Meta-labelling
                EnableMetaFilter = true;
                MetaBurnInSamples = 40;
                MetaProbThreshold = 0.50;
                MetaMinSkill = 0.0;
                MetaLearningRate = 0.05;
                MetaL2 = 0.001;
                MetaLabelR = 1.0;
                MetaConfidenceSizing = true;

                // News
                RecurringBlackoutsEt = "08:30;10:00";
                EventBlackoutsEt = string.Empty;
                NewsMinutesBefore = 2;
                NewsMinutesAfter = 5;
                FlattenBeforeEvents = true;

                // Diagnostics
                EnableJournal = true;
                JournalHistorical = false;
                ShowStatus = true;
                VerboseLog = false;

                AddPlot(Brushes.Goldenrod, "Vwap");
                AddPlot(Brushes.DodgerBlue, "NoiseUpper");
                AddPlot(Brushes.DodgerBlue, "NoiseLower");
                AddPlot(Brushes.DimGray, "OrHigh");
                AddPlot(Brushes.DimGray, "OrLow");
            }
            else if (State == State.Configure)
            {
                rules = new ChallengeRules
                {
                    StartingBalance = StartingBalance,
                    ProfitTarget = ProfitTargetUsd,
                    MaxLossLimit = MaxLossLimitUsd,
                    LockOffset = ThresholdLockOffsetUsd,
                    ConsistencyFraction = StopAtProfitTarget ? ConsistencyPercent / 100.0 : 0.0,
                    MinTradingDays = MinTradingDays,
                    StopAtProfitTarget = StopAtProfitTarget
                };
            }
            else if (State == State.DataLoaded)
            {
                InitialiseComponents();
            }
            else if (State == State.Realtime)
            {
                entryOrder = ToRealtime(entryOrder);
                scalpStopOrder = ToRealtime(scalpStopOrder);
                scalpTargetOrder = ToRealtime(scalpTargetOrder);
                runnerStopOrder = ToRealtime(runnerStopOrder);
                runnerTargetOrder = ToRealtime(runnerTargetOrder);
                exitOrder = ToRealtime(exitOrder);

                if (!configurationValid) return;
                if (tradeActive) tradeIsVirtual = true;
                PrintBacktestSummary();
                ActivateLiveState();
            }
            else if (State == State.Terminated)
            {
                if (configurationValid && !liveStateActive && attemptStats != null)
                    PrintBacktestSummary();
                if (liveStateActive) SaveState();
            }
        }

        private Order ToRealtime(Order order)
        {
            return order != null ? GetRealtimeOrder(order) : null;
        }

        private void InitialiseComponents()
        {
            configurationValid = true;
            configurationError = null;

            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                Fail("Primary data series must be 1 Minute bars (current: " + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType + ").");

            easternTz = FindEasternTimeZone();
            if (easternTz == null) Fail("Eastern time zone could not be resolved on this machine.");

            if (FlattenTimeEt <= LastEntryTimeEt) Fail("FlattenTimeEt must be later than LastEntryTimeEt.");
            if (OrbEntryEndEt > LastEntryTimeEt) Fail("OrbEntryEndEt must not be later than LastEntryTimeEt.");
            if (StopAtProfitTarget && ConsistencyPercent > 0 && DailyProfitCapUsd > ProfitTargetUsd * ConsistencyPercent / 100.0)
                Fail("DailyProfitCapUsd (" + DailyProfitCapUsd + ") exceeds the consistency cap ("
                     + (ProfitTargetUsd * ConsistencyPercent / 100.0) + "); a single day would raise the profit target.");
            if (RiskPerTradeUsd > DailyLossLimitUsd) Fail("RiskPerTradeUsd must not exceed DailyLossLimitUsd.");
            if (DailyLossLimitUsd + ThresholdBufferUsd >= MaxLossLimitUsd)
                Fail("DailyLossLimitUsd + ThresholdBufferUsd must be smaller than MaxLossLimitUsd.");

            try
            {
                news = new NewsCalendar(RecurringBlackoutsEt, EventBlackoutsEt);
            }
            catch (FormatException ex)
            {
                Fail(ex.Message);
            }

            account = new ChallengeAccount(rules);
            attemptStats = new ChallengeAttemptStats();
            metaModel = new OnlineLogisticModel(FeatureCount, MetaLearningRate, MetaL2);
            labeler = new TripleBarrierLabeler(metaModel);
            noiseProfile = new MinuteProfileHistory(NoiseLookbackDays, SessionSlots);
            volumeProfile = new MinuteProfileHistory(NoiseLookbackDays, SessionSlots);
            dailyAtr = new WilderAtr(14);
            atr1m = ATR(14);
            sessionIterator = new SessionIterator(Bars);

            tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            int limit = MicroRoots.Contains(Instrument.MasterInstrument.Name) ? 30 : 3;
            maxContractsEffective = MaxContractsOverride > 0 ? Math.Min(MaxContractsOverride, limit) : limit;

            string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "FlexChallengeBot");
            string accountName = Account != null ? Sanitise(Account.Name) : "NoAccount";
            stateFilePath = Path.Combine(dir, "state_" + accountName + ".txt");
            if (EnableJournal)
            {
                try
                {
                    journal = new TradeJournal(Path.Combine(dir, "journal_" + accountName + "_" + Sanitise(Instrument.MasterInstrument.Name) + ".csv"));
                }
                catch (Exception ex)
                {
                    journal = null;
                    Log("FlexChallengeBot: trade journal disabled (" + ex.Message + ")", LogLevel.Warning);
                }
            }

            if (!configurationValid)
            {
                Log("FlexChallengeBot configuration error: " + configurationError, LogLevel.Error);
                Print("FlexChallengeBot configuration error: " + configurationError);
            }
            else
            {
                Print(string.Format(CultureInfo.InvariantCulture,
                    "FlexChallengeBot initialised on {0} | tick value {1:0.00} | max contracts {2} | state file {3}",
                    Instrument.FullName, tickValue, maxContractsEffective, stateFilePath));
            }
        }

        private void Fail(string message)
        {
            configurationValid = false;
            configurationError = configurationError == null ? message : configurationError + " | " + message;
        }

        private static TimeZoneInfo FindEasternTimeZone()
        {
            string[] ids = { "Eastern Standard Time", "America/New_York" };
            foreach (string id in ids)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }
            return null;
        }

        private static string Sanitise(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? string.Empty)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "Unnamed";
        }

        #endregion

        #region Live state

        private void ActivateLiveState()
        {
            liveStateActive = true;
            account = new ChallengeAccount(rules);

            ChallengeSnapshot snapshot = null;
            if (!ResetLiveState)
            {
                try
                {
                    snapshot = StateStore.Load(stateFilePath);
                }
                catch (Exception ex)
                {
                    liveStateActive = false;
                    liveStateBlocked = true;
                    string msg = "FlexChallengeBot: live state file could not be read (" + ex.Message
                                 + "). New entries are blocked. Fix or delete '" + stateFilePath + "', or enable ResetLiveState.";
                    Log(msg, LogLevel.Error);
                    Print(msg);
                    return;
                }
            }

            if (snapshot != null) account.Restore(snapshot);
            else account.Reset(StartingBalance);

            account.ApplyManualOverrides(ManualCurrentBalance, ManualHighestEodBalance, ManualBestDayProfit, ManualTradingDays);
            SyncBrokerBalanceIfFlat();

            DateTime today = TradingDayOf(ToEastern(NinjaTrader.Core.Globals.Now));
            if (account.CurrentDay < today) account.StartNewDay(today);
            if (ManualCurrentBalance > 0 || ManualHighestEodBalance > 0 || ManualBestDayProfit > 0) account.ClearDayHalt();

            SaveState();
            Print(string.Format(CultureInfo.InvariantCulture,
                "FlexChallengeBot LIVE | balance {0:0.00} | HWM(EOD) {1:0.00} | threshold {2:0.00} | best day {3:0.00} | trading days {4} | {5}",
                account.Balance, account.HighestEodBalance, account.Threshold, account.BestDayProfit, account.TradingDays,
                account.Passed ? "PASSED - trading halted" : account.Failed ? "FAILED - trading halted" : "active"));
        }

        private void SyncBrokerBalanceIfFlat()
        {
            if (!liveStateActive || BalanceSource != BalanceSourceMode.BrokerAccount || signedPos != 0 || Account == null) return;
            double cash = Account.Get(AccountItem.CashValue, Currency.UsDollar);
            if (cash > 0) account.SetBalance(cash);
            else Log("FlexChallengeBot: broker CashValue unavailable, keeping internal balance.", LogLevel.Warning);
        }

        private void SaveState()
        {
            if (!liveStateActive || account == null) return;
            try
            {
                StateStore.Save(stateFilePath, account.Snapshot());
            }
            catch (Exception ex)
            {
                Log("FlexChallengeBot: state could not be saved (" + ex.Message + ")", LogLevel.Error);
            }
        }

        #endregion

        #region Bar processing

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || !configurationValid || CurrentBar < 1) return;

            bool intrabar = State == State.Realtime || Bars.IsTickReplay;

            GuardRisk(intrabar);
            if (State == State.Realtime && flattenRequested) FlattenWatchdog();

            int k;
            if (!intrabar) k = 0;
            else
            {
                if (!IsFirstTickOfBar) return;
                k = 1;
            }
            if (CurrentBar < k + 1) return;
            if (Time[k] <= lastProcessedBarTime) return;
            lastProcessedBarTime = Time[k];

            ProcessClosedBar(k);
        }

        private void ProcessClosedBar(int k)
        {
            DateTime et = ToEastern(Time[k]);
            DateTime td = TradingDayOf(et);
            if (td != tradingDay) OnNewTradingDay(td, k);

            double minutesFromOpen = (et - et.Date.Add(RthOpen)).TotalMinutes;
            int slot = (int)Math.Round(minutesFromOpen) - 1;
            bool inRth = minutesFromOpen > 0.5 && et.TimeOfDay <= RthClose && slot >= 0 && slot < SessionSlots;

            // label resolution uses this bar before any new signal is added on it
            if (inRth) labeler.OnBar(High[k], Low[k], Close[k], et);

            if (inRth) UpdateRth(k, slot);
            else if (rthActive && !rthCommitted && et.TimeOfDay > RthClose) CommitRthDay();

            UpdatePlots(k, inRth);

            if (tradeActive) ManagePosition(k, et, slot, inRth);
            if (inRth) EvaluateEntries(k, et, slot);

            if (ShowStatus && State == State.Realtime && ChartControl != null) DrawStatus(et);
        }

        private void OnNewTradingDay(DateTime td, int k)
        {
            if (rthActive && !rthCommitted) CommitRthDay();

            tradingDay = td;
            rthActive = false;
            rthCommitted = false;
            orComplete = false;
            orbLongSignaled = false;
            orbShortSignaled = false;
            orbEntriesToday = 0;
            noiseEntriesToday = 0;
            vwap = double.NaN;
            noiseUpper = double.NaN;
            noiseLower = double.NaN;

            if (liveStateActive) SyncBrokerBalanceIfFlat();
            account.StartNewDay(td);

            if (!liveStateActive && account.IsFinished)
            {
                if (account.Passed)
                {
                    attemptStats.RecordPass(account.TradingDays);
                    Print(string.Format(CultureInfo.InvariantCulture,
                        "[SIM] Challenge attempt #{0} PASSED in {1} trading days ({2} calendar sessions), profit {3:0.00}, best day {4:0.00}",
                        attemptStats.Attempts, account.TradingDays, account.CalendarDays, account.TotalProfit, account.BestDayProfit));
                }
                else
                {
                    attemptStats.RecordFail();
                    Print(string.Format(CultureInfo.InvariantCulture,
                        "[SIM] Challenge attempt #{0} FAILED after {1} trading days, balance {2:0.00}, threshold {3:0.00}",
                        attemptStats.Attempts, account.TradingDays, account.Balance, account.Threshold));
                }
                account.Reset(StartingBalance);
                account.StartNewDay(td);
            }

            if (liveStateActive) SaveState();

            sessionEndEt = DateTime.MinValue;
            try
            {
                if (sessionIterator.GetNextSession(Time[k], true))
                    sessionEndEt = ToEastern(sessionIterator.ActualSessionEnd);
            }
            catch (Exception ex)
            {
                if (VerboseLog) Print("Session iterator unavailable: " + ex.Message);
            }
        }

        private void UpdateRth(int k, int slot)
        {
            if (!rthActive)
            {
                if (rthCommitted) return; // RTH bars after an early commit are ignored
                rthActive = true;
                dayOpen = Open[k];
                rthHigh = High[k];
                rthLow = Low[k];
                cumVolume = 0;
                cumPv = 0;
                orHigh = High[k];
                orLow = Low[k];
                orOpen = Open[k];
                orComplete = false;
            }

            rthHigh = Math.Max(rthHigh, High[k]);
            rthLow = Math.Min(rthLow, Low[k]);
            rthClose = Close[k];

            double typical = (High[k] + Low[k] + Close[k]) / 3.0;
            double vol = Volume[k];
            cumVolume += vol;
            cumPv += typical * vol;
            vwap = cumVolume > 0 ? cumPv / cumVolume : Close[k];

            if (slot < OrMinutes)
            {
                orHigh = Math.Max(orHigh, High[k]);
                orLow = Math.Min(orLow, Low[k]);
                orClose = Close[k];
                if (slot == OrMinutes - 1) orComplete = true;
            }
            else if (!orComplete)
            {
                orComplete = true; // data gap inside the opening range: use what was observed
            }

            if (dayOpen > 0) noiseProfile.Record(slot, Math.Abs(Close[k] / dayOpen - 1.0));
            volumeProfile.Record(slot, cumVolume);

            double sigma = noiseProfile.Mean(slot);
            if (!double.IsNaN(sigma) && !double.IsNaN(prevRthClose))
            {
                double upRef = Math.Max(dayOpen, prevRthClose);
                double dnRef = Math.Min(dayOpen, prevRthClose);
                noiseUpper = upRef * (1.0 + NoiseBandMultiplier * sigma);
                noiseLower = dnRef * (1.0 - NoiseBandMultiplier * sigma);
            }
            else
            {
                noiseUpper = double.NaN;
                noiseLower = double.NaN;
            }
        }

        private void CommitRthDay()
        {
            dailyAtr.Update(rthHigh, rthLow, prevRthClose);
            prevRthOpen = dayOpen;
            prevRthClose = rthClose;
            noiseProfile.CommitDay(MinValidSlotsPerSession);
            volumeProfile.CommitDay(MinValidSlotsPerSession);
            labeler.ExpireAll(rthClose);
            rthCommitted = true;
            rthActive = false;
            vwap = double.NaN;
            noiseUpper = double.NaN;
            noiseLower = double.NaN;
        }

        private void UpdatePlots(int k, bool inRth)
        {
            if (!inRth) return;
            if (!double.IsNaN(vwap)) Values[0][k] = vwap;
            if (!double.IsNaN(noiseUpper)) Values[1][k] = noiseUpper;
            if (!double.IsNaN(noiseLower)) Values[2][k] = noiseLower;
            if (orComplete)
            {
                Values[3][k] = orHigh;
                Values[4][k] = orLow;
            }
        }

        #endregion

        #region Risk guard (every price change)

        private void GuardRisk(bool intrabar)
        {
            if (!tradeActive || tradeIsVirtual || signedPos == 0 || flattenRequested) return;

            double worstPx;
            double bestPx;
            if (intrabar)
            {
                worstPx = Close[0];
                bestPx = Close[0];
            }
            else
            {
                double slip = SlippageTicksForSizing * TickSize;
                if (signedPos > 0)
                {
                    worstPx = Math.Max(Low[0], currentStopPrice - slip);
                    bestPx = High[0];
                }
                else
                {
                    worstPx = Math.Min(High[0], currentStopPrice + slip);
                    bestPx = Low[0];
                }
            }

            double worstPnl = TradePnl(worstPx);
            double bestPnl = TradePnl(bestPx);
            double worstEquity = account.Balance + worstPnl;
            double worstDay = account.DayRealized + worstPnl;
            double bestDay = account.DayRealized + bestPnl;

            if (account.CheckEquityBreach(worstEquity))
            {
                RequestFlatten("Trailing threshold breached");
                Log(string.Format(CultureInfo.InvariantCulture, "FlexChallengeBot: equity {0:0.00} breached threshold {1:0.00}", worstEquity, account.Threshold), LogLevel.Alert);
                return;
            }
            if (worstEquity <= account.Threshold + ThresholdBufferUsd)
            {
                account.DayHalt = HaltReason.FloorProximity;
                RequestFlatten("Threshold buffer reached");
                return;
            }
            if (worstDay <= -DailyLossLimitUsd)
            {
                account.DayHalt = HaltReason.DailyLossLimit;
                RequestFlatten("Daily loss limit");
                return;
            }
            if (DailyProfitCapUsd > 0 && bestDay >= DailyProfitCapUsd)
            {
                account.DayHalt = HaltReason.DailyProfitCap;
                RequestFlatten("Daily profit cap (consistency)");
                return;
            }
            if (StopAtProfitTarget && account.TotalProfit + bestPnl >= account.EffectiveTarget(bestDay) + PassBufferUsd)
            {
                RequestFlatten("Profit target reached");
            }
        }

        private double TradePnl(double price)
        {
            return tradeCash + signedPos * price * Instrument.MasterInstrument.PointValue - tradeCommission;
        }

        #endregion

        #region Position management (closed bars)

        private void ManagePosition(int k, DateTime et, int slot, bool inRth)
        {
            if (!tradeActive || signedPos == 0 || flattenRequested) return;
            double close = Close[k];

            double mfe = tradeDir > 0 ? High[k] - entryAvg : entryAvg - Low[k];
            if (mfe > tradeMfePoints) tradeMfePoints = mfe;

            if (et >= FlattenDeadline(et))
            {
                RequestFlatten("Session flatten time");
                return;
            }
            if ((et - tradeEntryEt).TotalMinutes >= MaxHoldMinutes)
            {
                RequestFlatten("Max hold time");
                return;
            }
            if (FlattenBeforeEvents && news.DatedEventWithin(et, NewsMinutesBefore + 1))
            {
                RequestFlatten("High impact event");
                return;
            }
            if (!inRth) return;

            double atr = atr1m[k] > 0 ? atr1m[k] : TickSize * 4;
            double candidate = double.NaN;

            if (tradeModule == SignalModule.NoiseAreaMomentum)
            {
                double reference = NoiseTrailReference(tradeDir);
                if (!double.IsNaN(reference))
                {
                    if (IsNoiseCheckpoint(slot) && (tradeDir > 0 ? close < reference : close > reference))
                    {
                        RequestFlatten("Noise checkpoint exit");
                        return;
                    }
                    candidate = tradeDir > 0 ? reference - NoiseStopBufferAtr * atr : reference + NoiseStopBufferAtr * atr;
                }
            }
            else
            {
                bool active = scalpDone || tradeMfePoints >= TrailActivationR * actualRiskPoints;
                if (active)
                {
                    candidate = tradeDir > 0 ? close - TrailAtrMultiplier * atr : close + TrailAtrMultiplier * atr;
                    if (!double.IsNaN(vwap))
                    {
                        double v = tradeDir > 0 ? vwap - VwapTrailOffsetTicks * TickSize : vwap + VwapTrailOffsetTicks * TickSize;
                        if (tradeDir > 0 && v < close) candidate = Math.Max(candidate, v);
                        if (tradeDir < 0 && v > close) candidate = Math.Min(candidate, v);
                    }
                }
            }

            if (double.IsNaN(candidate)) return;
            candidate = Instrument.MasterInstrument.RoundToTickSize(candidate);
            bool improves = tradeDir > 0 ? candidate >= currentStopPrice + TickSize : candidate <= currentStopPrice - TickSize;
            if (improves) MoveStops(candidate);
        }

        private double NoiseTrailReference(int dir)
        {
            if (double.IsNaN(vwap)) return double.NaN;
            if (dir > 0) return double.IsNaN(noiseUpper) ? vwap : Math.Max(noiseUpper, vwap);
            return double.IsNaN(noiseLower) ? vwap : Math.Min(noiseLower, vwap);
        }

        private bool IsNoiseCheckpoint(int slot)
        {
            return NoiseCheckIntervalMinutes > 0 && (slot + 1) % NoiseCheckIntervalMinutes == 0;
        }

        private DateTime FlattenDeadline(DateTime et)
        {
            DateTime d = et.Date.Add(HhmmToTime(FlattenTimeEt));
            if (sessionEndEt != DateTime.MinValue && sessionEndEt.Date == et.Date)
            {
                DateTime alt = sessionEndEt.AddMinutes(-FlattenMinutesBeforeSessionEnd);
                if (alt < d) d = alt;
            }
            return d;
        }

        private DateTime LastEntryDeadline(DateTime et)
        {
            DateTime d = et.Date.Add(HhmmToTime(LastEntryTimeEt));
            DateTime flat = FlattenDeadline(et).AddMinutes(-15);
            return flat < d ? flat : d;
        }

        #endregion

        #region Signals and entries

        private void EvaluateEntries(int k, DateTime et, int slot)
        {
            bool atrReady = dailyAtr.IsReady && volumeProfile.IsReady;
            if (!atrReady)
            {
                if (State == State.Realtime && !dataReadyAnnounced)
                {
                    dataReadyAnnounced = true;
                    Print(string.Format(CultureInfo.InvariantCulture,
                        "FlexChallengeBot: not enough history (ATR days {0}/14, profile days {1}/{2}). Increase 'Days to load'.",
                        dailyAtr.Count, volumeProfile.CompletedDays, NoiseLookbackDays));
                }
                return;
            }

            var signals = new List<Signal>(2);
            Signal orb = EnableOrb ? BuildOrbSignal(k, et, slot) : null;
            if (orb != null) signals.Add(orb);
            Signal noise = EnableNoise ? BuildNoiseSignal(k, et, slot) : null;
            if (noise != null) signals.Add(noise);
            if (signals.Count == 0) return;

            DateTime labelExpiry = FlattenDeadline(et);
            bool traded = false;
            foreach (Signal s in signals)
            {
                double[] features = BuildFeatures(s, k, slot);
                double p = metaModel.Predict(features);
                bool modelActive = EnableMetaFilter && metaModel.Samples >= MetaBurnInSamples && metaModel.Skill > MetaMinSkill;
                labeler.Add(features, s.Direction, s.ReferencePrice, s.RiskPoints, MetaLabelR, labelExpiry);

                if (traded) continue;
                string blocked = EntryBlockReason(et, s);
                if (blocked != null)
                {
                    if (VerboseLog) Print(et.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " signal " + s.Description + " skipped: " + blocked);
                    continue;
                }
                if (modelActive && p < MetaProbThreshold)
                {
                    if (VerboseLog)
                        Print(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm} signal {1} filtered by meta model p={2:0.000} (skill {3:0.000})",
                            et, s.Description, p, metaModel.Skill));
                    continue;
                }

                double sizeMultiplier = 1.0;
                if (modelActive && MetaConfidenceSizing)
                {
                    double span = Math.Max(0.05, 0.70 - MetaProbThreshold);
                    sizeMultiplier = Math.Max(0.5, Math.Min(1.0, 0.5 + 0.5 * (p - MetaProbThreshold) / span));
                }
                traded = OpenTrade(s, features, modelActive ? p : double.NaN, sizeMultiplier, et);
            }
        }

        private Signal BuildOrbSignal(int k, DateTime et, int slot)
        {
            if (!orComplete || slot < OrMinutes || et.TimeOfDay > HhmmToTime(OrbEntryEndEt)) return null;
            if (orbEntriesToday >= OrbMaxEntriesPerDay) return null;

            double width = orHigh - orLow;
            double atrD = dailyAtr.Value;
            if (width <= 0 || atrD <= 0) return null;
            double close = Close[k];

            int dir = 0;
            if (!orbLongSignaled && close > orHigh)
            {
                orbLongSignaled = true;
                dir = 1;
            }
            else if (!orbShortSignaled && close < orLow)
            {
                orbShortSignaled = true;
                dir = -1;
            }
            if (dir == 0) return null;

            if (width < OrbMinWidthAtr * atrD || width > OrbMaxWidthAtr * atrD) return null;
            double extension = dir > 0 ? close - orHigh : orLow - close;
            if (extension > OrbMaxExtensionFraction * width) return null;
            if (OrbRequireVwapSide && !double.IsNaN(vwap) && (dir > 0 ? close <= vwap : close >= vwap)) return null;
            if (RelativeVolume(slot) < OrbMinRelativeVolume) return null;

            double stop = dir > 0
                ? orHigh - OrbStopFraction * width - TickSize
                : orLow + OrbStopFraction * width + TickSize;
            stop = Instrument.MasterInstrument.RoundToTickSize(stop);
            double risk = Math.Abs(close - stop);
            if (risk < MinStopTicks * TickSize)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(close - dir * MinStopTicks * TickSize);
                risk = Math.Abs(close - stop);
            }

            return new Signal
            {
                Module = SignalModule.OpeningRangeBreakout,
                Direction = dir,
                ReferencePrice = close,
                StopPrice = stop,
                RiskPoints = risk,
                Description = string.Format(CultureInfo.InvariantCulture, "ORB {0} OR[{1:0.00}-{2:0.00}]", dir > 0 ? "long" : "short", orLow, orHigh)
            };
        }

        private Signal BuildNoiseSignal(int k, DateTime et, int slot)
        {
            if (double.IsNaN(noiseUpper) || double.IsNaN(noiseLower) || double.IsNaN(vwap)) return null;
            if (!noiseProfile.IsReady || !IsNoiseCheckpoint(slot) || et.TimeOfDay < HhmmToTime(NoiseFirstCheckEt)) return null;
            if (noiseEntriesToday >= NoiseMaxEntriesPerDay) return null;

            double close = Close[k];
            int dir = 0;
            if (close > noiseUpper && close > vwap) dir = 1;
            else if (close < noiseLower && close < vwap) dir = -1;
            if (dir == 0) return null;
            if (tradeActive && tradeDir == dir) return null; // already riding this move

            double atr = atr1m[k] > 0 ? atr1m[k] : TickSize * 4;
            double reference = NoiseTrailReference(dir);
            double stop = dir > 0 ? reference - NoiseStopBufferAtr * atr : reference + NoiseStopBufferAtr * atr;
            stop = Instrument.MasterInstrument.RoundToTickSize(stop);
            double risk = dir > 0 ? close - stop : stop - close;
            if (risk < MinStopTicks * TickSize)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(close - dir * MinStopTicks * TickSize);
                risk = Math.Abs(close - stop);
            }

            return new Signal
            {
                Module = SignalModule.NoiseAreaMomentum,
                Direction = dir,
                ReferencePrice = close,
                StopPrice = stop,
                RiskPoints = risk,
                Description = string.Format(CultureInfo.InvariantCulture, "NOISE {0} band[{1:0.00}-{2:0.00}]", dir > 0 ? "long" : "short", noiseLower, noiseUpper)
            };
        }

        private string EntryBlockReason(DateTime et, Signal s)
        {
            if (State == State.Realtime && !liveStateActive) return liveStateBlocked ? "live state file unreadable" : "live state not active";
            if (tradeActive || AnyOrderAlive()) return "position or orders active";
            if (account.IsFinished) return account.Passed ? "challenge passed" : "challenge failed";
            if (account.DayHalt != HaltReason.None) return "day halted: " + account.DayHalt;
            if (account.TradesToday >= MaxTradesPerDay) return "max trades per day";
            if (account.ConsecutiveLossesToday >= MaxConsecutiveLosses) return "max consecutive losses";
            if (et > LastEntryDeadline(et)) return "after last entry time";
            if (news.IsBlackout(et, NewsMinutesBefore, NewsMinutesAfter)) return "news blackout";
            if (FlattenBeforeEvents && news.DatedEventWithin(et, Math.Max(30, NewsMinutesBefore))) return "high impact event ahead";
            if (s.RiskPoints > MaxStopAtrFraction * dailyAtr.Value) return "stop wider than MaxStopAtrFraction";
            if (s.RiskPoints < TickSize) return "invalid stop";
            return null;
        }

        private double[] BuildFeatures(Signal s, int k, int slot)
        {
            double atrD = Math.Max(dailyAtr.Value, TickSize);
            double atrM = atr1m[k] > 0 ? atr1m[k] : TickSize;
            double close = Close[k];
            int d = s.Direction;
            var f = new double[FeatureCount];
            f[0] = s.Module == SignalModule.OpeningRangeBreakout ? 1.0 : 0.0;
            f[1] = d;
            f[2] = orComplete ? (orHigh - orLow) / atrD : 0.0;
            f[3] = double.IsNaN(vwap) ? 0.0 : (close - vwap) / atrM * d;
            f[4] = Math.Log(Math.Max(RelativeVolume(slot), 0.05));
            f[5] = (slot + 1) / (double)SessionSlots;
            f[6] = atrM / atrD;
            f[7] = double.IsNaN(prevRthClose) ? 0.0 : (dayOpen - prevRthClose) / atrD * d;
            f[8] = double.IsNaN(prevRthClose) || double.IsNaN(prevRthOpen) ? 0.0 : (prevRthClose - prevRthOpen) / atrD * d;
            f[9] = EfficiencyRatio(k, EfficiencyLookback) * d;
            f[10] = (close - dayOpen) / atrD * d;
            f[11] = s.RiskPoints / atrD;
            f[12] = orComplete ? Math.Sign(orClose - orOpen) * d : 0.0;
            return f;
        }

        private double RelativeVolume(int slot)
        {
            double avg = volumeProfile.Mean(slot);
            if (double.IsNaN(avg) || avg <= 0) return 1.0;
            return cumVolume / avg;
        }

        private double EfficiencyRatio(int k, int n)
        {
            if (CurrentBar < k + n) return 0.0;
            double change = Close[k] - Close[k + n];
            double path = 0;
            for (int i = 0; i < n; i++) path += Math.Abs(Close[k + i] - Close[k + i + 1]);
            return path > 0 ? change / path : 0.0;
        }

        private double ComputeRiskBudget()
        {
            double thresholdRoom = (account.Balance - account.Threshold - ThresholdBufferUsd) * ThresholdRiskFraction;
            double dailyRoom = DailyLossLimitUsd + account.DayRealized;
            return Math.Min(RiskPerTradeUsd, Math.Min(thresholdRoom, dailyRoom));
        }

        private bool OpenTrade(Signal s, double[] features, double prob, double sizeMultiplier, DateTime et)
        {
            double stopTicks = s.RiskPoints / TickSize;
            double budget = ComputeRiskBudget() * sizeMultiplier;
            int n = RiskSizer.ContractsForRisk(budget, stopTicks, tickValue, SlippageTicksForSizing, CommissionPerContractRoundTurn, maxContractsEffective);

            double capRoom = DailyProfitCapUsd > 0 ? DailyProfitCapUsd - account.DayRealized : double.MaxValue;
            int nCap = RiskSizer.ContractsForProfitRoom(capRoom, stopTicks, tickValue, maxContractsEffective);
            int nTarget = maxContractsEffective;
            if (StopAtProfitTarget)
            {
                double targetRoom = account.EffectiveTarget(account.DayRealized) + PassBufferUsd - account.TotalProfit;
                nTarget = Math.Max(1, RiskSizer.ContractsForProfitRoom(targetRoom, stopTicks, tickValue, maxContractsEffective));
            }
            n = Math.Min(n, Math.Min(nCap, nTarget));

            if (n < 1)
            {
                if (VerboseLog)
                    Print(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm} {1} not sized: budget {2:0.00}, stop {3:0} ticks, cap room {4:0.00}",
                        et, s.Description, budget, stopTicks, capRoom));
                return false;
            }

            tradeSeq++;
            string tag = tradeSeq.ToString(CultureInfo.InvariantCulture);
            entryName = "FXE" + tag;
            scalpStopName = "FXSS" + tag;
            scalpTargetName = "FXST" + tag;
            runnerStopName = "FXRS" + tag;
            runnerTargetName = "FXRT" + tag;
            flattenName = "FXX" + tag;
            fixName = "FXF" + tag;

            tradeActive = true;
            tradeIsVirtual = false;
            tradeDir = s.Direction;
            tradeModule = s.Module;
            tradeFeatures = features;
            tradeProb = prob;
            scalpPlannedQty = ScalpFraction > 0 && ScalpTargetR > 0
                ? Math.Max(0, Math.Min(n, (int)Math.Floor(n * ScalpFraction + 0.5)))
                : 0;
            scalpOpen = 0;
            runnerOpen = 0;
            scalpAllocated = 0;
            entryFilled = 0;
            entryAvg = 0;
            initialStopPrice = s.StopPrice;
            currentStopPrice = s.StopPrice;
            actualRiskPoints = s.RiskPoints;
            tradeCash = 0;
            tradeCommission = 0;
            tradeMfePoints = 0;
            tradeEntryEt = et;
            scalpDone = false;
            flattenRequested = false;
            flattenRetries = 0;
            exitReason = null;

            if (s.Module == SignalModule.OpeningRangeBreakout) orbEntriesToday++;
            else noiseEntriesToday++;

            Print(string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm} ENTRY {1} x{2} ref {3:0.00} stop {4:0.00} risk {5:0.00}pt budget {6:0.00} p={7}",
                et, s.Description, n, s.ReferencePrice, s.StopPrice, s.RiskPoints, budget,
                double.IsNaN(prob) ? "n/a" : prob.ToString("0.000", CultureInfo.InvariantCulture)));

            entryOrder = SubmitOrderUnmanaged(0, tradeDir > 0 ? OrderAction.Buy : OrderAction.SellShort, OrderType.Market, n, 0, 0, string.Empty, entryName);
            return true;
        }

        #endregion

        #region Order and execution events

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            AssignReference(order);

            if (orderState == OrderState.Rejected)
            {
                HandleRejection(order, error, comment);
            }
            else if (error != ErrorCode.NoError && !Order.IsTerminalState(orderState))
            {
                Log("FlexChallengeBot: order " + order.Name + " error " + error + " (" + comment + ")", LogLevel.Warning);
            }

            if (Order.IsTerminalState(orderState))
            {
                ReleaseReference(order);
                // OnOrderUpdate(Filled) arrives before OnExecutionUpdate, so only a non-filled terminal state
                // with zero filled quantity means the entry really died.
                if (order.Name == entryName && orderState != OrderState.Filled && filled == 0 && entryFilled == 0 && signedPos == 0 && tradeActive)
                {
                    Print("Entry " + order.Name + " ended without fill (" + orderState + ")");
                    ResetTradeState();
                }
                if (orderState != OrderState.Filled && flattenRequested) TrySubmitFlattenOrder();
            }
        }

        private void AssignReference(Order order)
        {
            if (Order.IsTerminalState(order.OrderState)) return;
            string n = order.Name;
            if (n == entryName) entryOrder = order;
            else if (n == scalpStopName) scalpStopOrder = order;
            else if (n == scalpTargetName) scalpTargetOrder = order;
            else if (n == runnerStopName) runnerStopOrder = order;
            else if (n == runnerTargetName) runnerTargetOrder = order;
            else if (n == flattenName || n == fixName) exitOrder = order;
        }

        private void ReleaseReference(Order order)
        {
            if (order == entryOrder) entryOrder = null;
            if (order == scalpStopOrder) scalpStopOrder = null;
            if (order == scalpTargetOrder) scalpTargetOrder = null;
            if (order == runnerStopOrder) runnerStopOrder = null;
            if (order == runnerTargetOrder) runnerTargetOrder = null;
            if (order == exitOrder) exitOrder = null;
            if (order == neutraliseOrder) neutraliseOrder = null;
        }

        private void HandleRejection(Order order, ErrorCode error, string comment)
        {
            string msg = "FlexChallengeBot: order " + order.Name + " REJECTED (" + error + ": " + comment + ")";
            Log(msg, LogLevel.Error);
            Print(msg);

            string n = order.Name;
            if (n == entryName)
            {
                if (signedPos != 0) RequestFlatten("Entry rejected after partial fill");
                return;
            }
            if (n == flattenName || n == fixName)
            {
                flattenRetries++;
                if (flattenRetries > MaxFlattenRetries)
                    Log("FlexChallengeBot: flatten order rejected " + flattenRetries + " times - FLATTEN MANUALLY", LogLevel.Alert);
                return;
            }
            if (signedPos != 0) RequestFlatten("Protective order rejected");
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            Order o = execution.Order;
            if (o == null || quantity <= 0) return;

            int signedQty = o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover ? quantity : -quantity;
            double commission = execution.Commission > 0 ? execution.Commission : CommissionPerContractRoundTurn * 0.5 * quantity;
            signedPos += signedQty;

            if (!tradeActive)
            {
                // Execution outside a managed trade (e.g. a session-close exit racing a finalised trade): neutralise it.
                if (signedPos != 0 && !IsAlive(neutraliseOrder)) EmergencyNeutralise("Unmanaged execution " + o.Name);
                return;
            }

            tradeCash -= signedQty * price * Instrument.MasterInstrument.PointValue;
            tradeCommission += commission;

            string n = o.Name;
            if (n == entryName)
            {
                OnEntryFill(price, quantity);
            }
            else
            {
                OnExitFill(n, quantity);
            }

            if (signedPos != 0 && Math.Sign(signedPos) != tradeDir)
            {
                Log("FlexChallengeBot: overfill detected on " + n + ", correcting " + signedPos, LogLevel.Alert);
                CancelAllOrders();
                exitOrder = SubmitOrderUnmanaged(0, signedPos > 0 ? OrderAction.Sell : OrderAction.BuyToCover,
                    OrderType.Market, Math.Abs(signedPos), 0, 0, string.Empty, fixName);
                return;
            }

            if (signedPos == 0 && entryFilled > 0 && n != entryName)
            {
                FinalizeTrade(time);
                return;
            }

            if (flattenRequested) TrySubmitFlattenOrder();
        }

        private void OnEntryFill(double price, int quantity)
        {
            bool first = entryFilled == 0;
            entryAvg = (entryAvg * entryFilled + price * quantity) / (entryFilled + quantity);
            entryFilled += quantity;

            if (first)
            {
                if (!tradeIsVirtual) account.RegisterTradeOpened();
                actualRiskPoints = Math.Max(TickSize, Math.Abs(entryAvg - initialStopPrice));
                scalpTargetPrice = Instrument.MasterInstrument.RoundToTickSize(entryAvg + tradeDir * ScalpTargetR * actualRiskPoints);
                runnerTargetPrice = RunnerTargetR > 0
                    ? Instrument.MasterInstrument.RoundToTickSize(entryAvg + tradeDir * RunnerTargetR * actualRiskPoints)
                    : 0;
            }

            if (flattenRequested) return;

            if (!StopPriceValid(currentStopPrice))
            {
                RequestFlatten("Stop already crossed at fill");
                return;
            }

            int toScalp = Math.Min(quantity, Math.Max(0, scalpPlannedQty - scalpAllocated));
            int toRunner = quantity - toScalp;
            if (toScalp > 0)
            {
                scalpAllocated += toScalp;
                scalpOpen += toScalp;
                SyncLeg(true);
            }
            if (toRunner > 0)
            {
                runnerOpen += toRunner;
                SyncLeg(false);
            }
        }

        private void OnExitFill(string name, int quantity)
        {
            if (name == scalpStopName || name == scalpTargetName)
            {
                scalpOpen = Math.Max(0, scalpOpen - quantity);
                if (name == scalpTargetName && scalpOpen == 0 && !scalpDone)
                {
                    scalpDone = true;
                    exitReason = "Scalp target";
                    double be = Instrument.MasterInstrument.RoundToTickSize(entryAvg + tradeDir * BreakevenOffsetTicks * TickSize);
                    bool better = tradeDir > 0 ? be > currentStopPrice : be < currentStopPrice;
                    if (better && runnerOpen > 0 && !flattenRequested) MoveStops(be);
                }
                else if (name == scalpStopName) exitReason = "Stop";
                if (!flattenRequested) SyncLeg(true);
            }
            else if (name == runnerStopName || name == runnerTargetName)
            {
                runnerOpen = Math.Max(0, runnerOpen - quantity);
                exitReason = name == runnerTargetName ? "Runner target" : (scalpDone ? "Trailing stop" : "Stop");
                if (!flattenRequested) SyncLeg(false);
            }
            else
            {
                int fromRunner = Math.Min(runnerOpen, quantity);
                runnerOpen -= fromRunner;
                scalpOpen = Math.Max(0, scalpOpen - (quantity - fromRunner));
                if (exitReason == null || name == flattenName) exitReason = flattenRequested ? flattenReasonText : "Exit " + name;
            }
        }

        /// <summary>Creates or resizes the OCO stop/target pair of a leg to the leg's open quantity.</summary>
        private void SyncLeg(bool scalp)
        {
            int open = scalp ? scalpOpen : runnerOpen;
            Order stop = scalp ? scalpStopOrder : runnerStopOrder;
            Order target = scalp ? scalpTargetOrder : runnerTargetOrder;
            OrderAction exitAction = tradeDir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;

            if (open <= 0)
            {
                SafeCancel(stop);
                SafeCancel(target);
                return;
            }

            if (stop == null && target == null)
            {
                ocoNonce++;
                string oco = "FXOCO" + (scalp ? "S" : "R") + tradeSeq.ToString(CultureInfo.InvariantCulture) + "_" +
                             ocoNonce.ToString(CultureInfo.InvariantCulture) + "_" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                double tgtPrice = scalp ? scalpTargetPrice : runnerTargetPrice;
                Order s = SubmitOrderUnmanaged(0, exitAction, OrderType.StopMarket, open, 0, currentStopPrice, oco,
                    scalp ? scalpStopName : runnerStopName);
                Order t = null;
                if (tgtPrice > 0)
                    t = SubmitOrderUnmanaged(0, exitAction, OrderType.Limit, open, tgtPrice, 0, oco,
                        scalp ? scalpTargetName : runnerTargetName);
                if (scalp)
                {
                    if (scalpStopOrder == null) scalpStopOrder = s;
                    if (scalpTargetOrder == null) scalpTargetOrder = t;
                }
                else
                {
                    if (runnerStopOrder == null) runnerStopOrder = s;
                    if (runnerTargetOrder == null) runnerTargetOrder = t;
                }
                return;
            }

            ResizeOrder(stop, open);
            ResizeOrder(target, open);
        }

        private void ResizeOrder(Order order, int openQuantity)
        {
            if (!IsChangeable(order)) return;
            int desired = order.Filled + openQuantity;
            if (desired != order.Quantity)
                ChangeOrder(order, desired, order.LimitPrice, order.StopPrice);
        }

        private void MoveStops(double newStop)
        {
            if (!StopPriceValid(newStop))
            {
                RequestFlatten("Trailing level crossed");
                return;
            }
            currentStopPrice = newStop;
            if (IsChangeable(scalpStopOrder) && Math.Abs(scalpStopOrder.StopPrice - newStop) >= TickSize * 0.5)
                ChangeOrder(scalpStopOrder, scalpStopOrder.Quantity, 0, newStop);
            if (IsChangeable(runnerStopOrder) && Math.Abs(runnerStopOrder.StopPrice - newStop) >= TickSize * 0.5)
                ChangeOrder(runnerStopOrder, runnerStopOrder.Quantity, 0, newStop);
        }

        private bool StopPriceValid(double stop)
        {
            if (tradeDir > 0)
            {
                double bid = State == State.Realtime ? GetCurrentBid() : Close[0];
                return stop < bid;
            }
            double ask = State == State.Realtime ? GetCurrentAsk() : Close[0];
            return stop > ask;
        }

        private static bool IsAlive(Order o)
        {
            return o != null && !Order.IsTerminalState(o.OrderState);
        }

        private static bool CanCancel(Order o)
        {
            if (o == null || Order.IsTerminalState(o.OrderState)) return false;
            return o.OrderState != OrderState.CancelPending && o.OrderState != OrderState.CancelSubmitted;
        }

        private void SafeCancel(Order o)
        {
            if (CanCancel(o)) CancelOrder(o);
        }

        private static bool IsChangeable(Order o)
        {
            if (o == null) return false;
            OrderState s = o.OrderState;
            return s == OrderState.Accepted || s == OrderState.Working || s == OrderState.TriggerPending || s == OrderState.PartFilled;
        }

        private bool AnyOrderAlive()
        {
            return IsAlive(entryOrder) || IsAlive(scalpStopOrder) || IsAlive(scalpTargetOrder) ||
                   IsAlive(runnerStopOrder) || IsAlive(runnerTargetOrder) || IsAlive(exitOrder);
        }

        private bool AnyLegAlive()
        {
            return IsAlive(scalpStopOrder) || IsAlive(scalpTargetOrder) || IsAlive(runnerStopOrder) || IsAlive(runnerTargetOrder);
        }

        private void CancelAllOrders()
        {
            SafeCancel(entryOrder);
            SafeCancel(scalpStopOrder);
            SafeCancel(scalpTargetOrder);
            SafeCancel(runnerStopOrder);
            SafeCancel(runnerTargetOrder);
        }

        private void RequestFlatten(string reason)
        {
            if (flattenRequested) return;
            SafeCancel(entryOrder);
            if (signedPos == 0) return;

            flattenRequested = true;
            flattenReasonText = reason;
            flattenRequestedUtc = DateTime.UtcNow;
            Print(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} FLATTEN requested: {1} (position {2})",
                ToEastern(Time[0]), reason, signedPos));
            CancelAllOrders();
            TrySubmitFlattenOrder();
        }

        private void TrySubmitFlattenOrder()
        {
            if (!flattenRequested || signedPos == 0 || IsAlive(exitOrder)) return;
            bool waitForCancels = State == State.Realtime && AnyLegAlive() &&
                                  (DateTime.UtcNow - flattenRequestedUtc).TotalSeconds < FlattenWatchdogSeconds;
            if (waitForCancels) return;
            if (flattenRetries > MaxFlattenRetries) return;

            exitOrder = SubmitOrderUnmanaged(0, signedPos > 0 ? OrderAction.Sell : OrderAction.BuyToCover,
                OrderType.Market, Math.Abs(signedPos), 0, 0, string.Empty, flattenName);
        }

        private void FlattenWatchdog()
        {
            if (signedPos == 0) return;
            if (!IsAlive(exitOrder)) TrySubmitFlattenOrder();
        }

        private void EmergencyNeutralise(string why)
        {
            Log("FlexChallengeBot: " + why + " - neutralising position " + signedPos, LogLevel.Alert);
            string name = "FXN" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            neutraliseOrder = SubmitOrderUnmanaged(0, signedPos > 0 ? OrderAction.Sell : OrderAction.BuyToCover,
                OrderType.Market, Math.Abs(signedPos), 0, 0, string.Empty, name);
        }

        private void FinalizeTrade(DateTime time)
        {
            CancelAllOrders();

            double pnl = tradeCash - tradeCommission;
            double riskUsd = actualRiskPoints * Instrument.MasterInstrument.PointValue * Math.Max(1, entryFilled);
            double r = riskUsd > 0 ? pnl / riskUsd : 0;
            string reason = exitReason ?? (flattenRequested ? flattenReasonText : "Exit");
            DateTime et = ToEastern(time);

            if (!tradeIsVirtual)
            {
                account.RegisterTradeClosed(pnl);
                if (liveStateActive) SaveState();
                if (journal != null && (liveStateActive || JournalHistorical))
                {
                    try
                    {
                        journal.Write(et, tradeModule, tradeDir, entryFilled, entryAvg, initialStopPrice, actualRiskPoints, pnl, r,
                            tradeProb, reason, account.Balance, account.Threshold, account.DayRealized, tradeFeatures);
                    }
                    catch (Exception ex)
                    {
                        Log("FlexChallengeBot: journal write failed (" + ex.Message + ")", LogLevel.Warning);
                    }
                }
            }

            Print(string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss} EXIT {1} {2} x{3} pnl {4:0.00} ({5:0.00}R) reason: {6} | balance {7:0.00} day {8:0.00} threshold {9:0.00}{10}",
                et, tradeModule, tradeDir > 0 ? "long" : "short", entryFilled, pnl, r, reason,
                account.Balance, account.DayRealized, account.Threshold, tradeIsVirtual ? " [virtual]" : string.Empty));

            if (!tradeIsVirtual)
            {
                if (account.Passed)
                {
                    string m = string.Format(CultureInfo.InvariantCulture, "CHALLENGE TARGET REACHED: profit {0:0.00}, effective target {1:0.00}. Trading halted.",
                        account.TotalProfit, account.EffectiveTarget(account.DayRealized));
                    Print(m);
                    if (liveStateActive) Log("FlexChallengeBot: " + m, LogLevel.Alert);
                }
                else if (account.Failed)
                {
                    Print("CHALLENGE THRESHOLD BREACHED. Trading halted.");
                    if (liveStateActive) Log("FlexChallengeBot: challenge threshold breached", LogLevel.Alert);
                }
            }

            ResetTradeState();
        }

        private void ResetTradeState()
        {
            tradeActive = false;
            tradeIsVirtual = false;
            tradeDir = 0;
            scalpPlannedQty = 0;
            scalpOpen = 0;
            runnerOpen = 0;
            scalpAllocated = 0;
            entryFilled = 0;
            entryAvg = 0;
            tradeCash = 0;
            tradeCommission = 0;
            flattenRequested = false;
            flattenRetries = 0;
            exitReason = null;
            tradeFeatures = null;
            tradeProb = double.NaN;
        }

        #endregion

        #region Helpers and display

        private DateTime ToEastern(DateTime platformTime)
        {
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified),
                NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, easternTz);
        }

        private static DateTime TradingDayOf(DateTime et)
        {
            return et.Hour >= TradingDayRolloverHourEt ? et.Date.AddDays(1) : et.Date;
        }

        private static TimeSpan HhmmToTime(int hhmm)
        {
            return new TimeSpan(hhmm / 100, hhmm % 100, 0);
        }

        private void PrintBacktestSummary()
        {
            if (summaryPrinted || attemptStats == null) return;
            summaryPrinted = true;
            Print("================ FlexChallengeBot challenge simulation ================");
            Print(string.Format(CultureInfo.InvariantCulture, "Completed attempts: {0} | passed {1} | failed {2} | pass rate {3:P1}",
                attemptStats.Attempts, attemptStats.Passed, attemptStats.Failed, attemptStats.PassRate));
            Print(string.Format(CultureInfo.InvariantCulture, "Trading days to pass: median {0:0.0} | mean {1:0.0}",
                attemptStats.MedianDaysToPass, attemptStats.MeanDaysToPass));
            Print(string.Format(CultureInfo.InvariantCulture, "Open attempt: balance {0:0.00} | threshold {1:0.00} | trading days {2}",
                account.Balance, account.Threshold, account.TradingDays));
            Print(string.Format(CultureInfo.InvariantCulture, "Meta model: {0} labelled samples | base rate {1:P1} | Brier {2:0.0000} vs baseline {3:0.0000} | skill {4:0.000}",
                metaModel.Samples, metaModel.BaseRate, metaModel.PrequentialBrier, metaModel.BaselineBrier, metaModel.Skill));
            Print("=======================================================================");
        }

        private void DrawStatus(DateTime et)
        {
            double open = tradeActive && !tradeIsVirtual ? TradePnl(Close[0]) : 0;
            double day = account.DayRealized + open;
            bool modelActive = EnableMetaFilter && metaModel.Samples >= MetaBurnInSamples && metaModel.Skill > MetaMinSkill;
            string text = string.Format(CultureInfo.InvariantCulture,
                "FlexChallengeBot  {0:HH:mm} ET\n" +
                "Balance {1:N2} | Threshold {2:N2} | Room {3:N2}\n" +
                "Day P/L {4:N2} | cap {5:N0} | loss limit {6:N0}\n" +
                "Profit {7:N2} / target {8:N2} | best day {9:N2}\n" +
                "Trades today {10}/{11} | Trading days {12} | Halt: {13}\n" +
                "Meta: {14} samples, skill {15:0.000} ({16})",
                et, account.Balance, account.Threshold, account.Balance + open - account.Threshold,
                day, DailyProfitCapUsd, DailyLossLimitUsd,
                account.TotalProfit + open, StopAtProfitTarget ? account.EffectiveTarget(day) : 0, account.BestDayProfit,
                account.TradesToday, MaxTradesPerDay, account.TradingDays,
                account.Passed ? "PASSED" : account.Failed ? "FAILED" : account.DayHalt.ToString(),
                metaModel.Samples, metaModel.Skill, modelActive ? "active" : "learning");
            Draw.TextFixed(this, "FlexChallengeBotStatus", text, TextPosition.TopLeft);
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Range(1000, double.MaxValue)]
        [Display(Name = "Starting balance", GroupName = "1. Challenge", Order = 1)]
        public double StartingBalance { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Profit target ($)", GroupName = "1. Challenge", Order = 2)]
        public double ProfitTargetUsd { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Max loss limit, EOD trailing ($)", GroupName = "1. Challenge", Order = 3)]
        public double MaxLossLimitUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Threshold lock offset ($)", Description = "Trailing threshold stops at starting balance + this value.", GroupName = "1. Challenge", Order = 4)]
        public double ThresholdLockOffsetUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Consistency rule (%)", Description = "Best day must stay below this share of the profit target. 0 = off.", GroupName = "1. Challenge", Order = 5)]
        public double ConsistencyPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Min trading days", GroupName = "1. Challenge", Order = 6)]
        public int MinTradingDays { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop at profit target", Description = "Challenge mode. Disable on a funded account (no target, no consistency).", GroupName = "1. Challenge", Order = 7)]
        public bool StopAtProfitTarget { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Balance source (live)", GroupName = "1. Challenge", Order = 8)]
        public BalanceSourceMode BalanceSource { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Manual current balance (0 = keep)", GroupName = "1. Challenge", Order = 9)]
        public double ManualCurrentBalance { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Manual highest EOD balance (0 = keep)", GroupName = "1. Challenge", Order = 10)]
        public double ManualHighestEodBalance { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Manual best day profit (0 = keep)", GroupName = "1. Challenge", Order = 11)]
        public double ManualBestDayProfit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Manual trading days (0 = keep)", GroupName = "1. Challenge", Order = 12)]
        public int ManualTradingDays { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reset live state", Description = "Ignore the persisted live state file (new challenge).", GroupName = "1. Challenge", Order = 13)]
        public bool ResetLiveState { get; set; }

        [NinjaScriptProperty]
        [Range(10, double.MaxValue)]
        [Display(Name = "Risk per trade ($)", GroupName = "2. Risk", Order = 1)]
        public double RiskPerTradeUsd { get; set; }

        [NinjaScriptProperty]
        [Range(10, double.MaxValue)]
        [Display(Name = "Daily loss limit ($)", GroupName = "2. Risk", Order = 2)]
        public double DailyLossLimitUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily profit cap ($, 0 = off)", Description = "Flatten and stop for the day; keeps the 40% consistency rule satisfied.", GroupName = "2. Risk", Order = 3)]
        public double DailyProfitCapUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Threshold buffer ($)", GroupName = "2. Risk", Order = 4)]
        public double ThresholdBufferUsd { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.0)]
        [Display(Name = "Max fraction of threshold room per trade", GroupName = "2. Risk", Order = 5)]
        public double ThresholdRiskFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Pass buffer ($)", GroupName = "2. Risk", Order = 6)]
        public double PassBufferUsd { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Max trades per day", GroupName = "2. Risk", Order = 7)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max consecutive losses per day", GroupName = "2. Risk", Order = 8)]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "Max contracts (0 = rule limit)", Description = "Flex 50K: 3 minis or 30 micros.", GroupName = "2. Risk", Order = 9)]
        public int MaxContractsOverride { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Commission per contract round turn ($)", GroupName = "2. Risk", Order = 10)]
        public double CommissionPerContractRoundTurn { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Slippage ticks for sizing", GroupName = "2. Risk", Order = 11)]
        public double SlippageTicksForSizing { get; set; }

        [NinjaScriptProperty]
        [Range(930, 1600)]
        [Display(Name = "Last entry time ET (HHmm)", GroupName = "3. Session", Order = 1)]
        public int LastEntryTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(930, 1659)]
        [Display(Name = "Flatten time ET (HHmm)", GroupName = "3. Session", Order = 2)]
        public int FlattenTimeEt { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Flatten minutes before session end", Description = "Handles holiday early closes from the trading hours template.", GroupName = "3. Session", Order = 3)]
        public int FlattenMinutesBeforeSessionEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable ORB", GroupName = "4. Opening range breakout", Order = 1)]
        public bool EnableOrb { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Opening range minutes", GroupName = "4. Opening range breakout", Order = 2)]
        public int OrMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(931, 1600)]
        [Display(Name = "ORB entry end ET (HHmm)", GroupName = "4. Opening range breakout", Order = 3)]
        public int OrbEntryEndEt { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 5.0)]
        [Display(Name = "Min OR width (x daily ATR)", GroupName = "4. Opening range breakout", Order = 4)]
        public double OrbMinWidthAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 5.0)]
        [Display(Name = "Max OR width (x daily ATR)", GroupName = "4. Opening range breakout", Order = 5)]
        public double OrbMaxWidthAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.2, 1.5)]
        [Display(Name = "Stop as fraction of OR (1 = opposite side)", GroupName = "4. Opening range breakout", Order = 6)]
        public double OrbStopFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 5.0)]
        [Display(Name = "Max breakout extension (x OR width)", GroupName = "4. Opening range breakout", Order = 7)]
        public double OrbMaxExtensionFraction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require VWAP side", GroupName = "4. Opening range breakout", Order = 8)]
        public bool OrbRequireVwapSide { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Min relative volume", GroupName = "4. Opening range breakout", Order = 9)]
        public double OrbMinRelativeVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2)]
        [Display(Name = "Max ORB entries per day", GroupName = "4. Opening range breakout", Order = 10)]
        public int OrbMaxEntriesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable noise-area momentum", GroupName = "5. Noise area momentum", Order = 1)]
        public bool EnableNoise { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "Lookback days", GroupName = "5. Noise area momentum", Order = 2)]
        public int NoiseLookbackDays { get; set; }

        [NinjaScriptProperty]
        [Range(0.3, 3.0)]
        [Display(Name = "Band multiplier", GroupName = "5. Noise area momentum", Order = 3)]
        public double NoiseBandMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(5, 120)]
        [Display(Name = "Checkpoint interval (minutes)", GroupName = "5. Noise area momentum", Order = 4)]
        public int NoiseCheckIntervalMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(935, 1600)]
        [Display(Name = "First checkpoint ET (HHmm)", GroupName = "5. Noise area momentum", Order = 5)]
        public int NoiseFirstCheckEt { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Hard stop buffer (x 1-min ATR)", GroupName = "5. Noise area momentum", Order = 6)]
        public double NoiseStopBufferAtr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max noise entries per day", GroupName = "5. Noise area momentum", Order = 7)]
        public int NoiseMaxEntriesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Scalp leg fraction", GroupName = "6. Exits", Order = 1)]
        public double ScalpFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Scalp target (R)", GroupName = "6. Exits", Order = 2)]
        public double ScalpTargetR { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "Runner target (R, 0 = trail only)", GroupName = "6. Exits", Order = 3)]
        public double RunnerTargetR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 40)]
        [Display(Name = "Breakeven offset ticks", GroupName = "6. Exits", Order = 4)]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "ORB trail activation (R)", GroupName = "6. Exits", Order = 5)]
        public double TrailActivationR { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 20.0)]
        [Display(Name = "ORB trail (x 1-min ATR)", GroupName = "6. Exits", Order = 6)]
        public double TrailAtrMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "VWAP trail offset ticks", GroupName = "6. Exits", Order = 7)]
        public int VwapTrailOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Min stop ticks", GroupName = "6. Exits", Order = 8)]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.02, 2.0)]
        [Display(Name = "Max stop (x daily ATR)", GroupName = "6. Exits", Order = 9)]
        public double MaxStopAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Range(5, 1440)]
        [Display(Name = "Max hold minutes", GroupName = "6. Exits", Order = 10)]
        public int MaxHoldMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable meta-label filter", GroupName = "7. Machine learning", Order = 1)]
        public bool EnableMetaFilter { get; set; }

        [NinjaScriptProperty]
        [Range(10, 10000)]
        [Display(Name = "Burn-in samples", GroupName = "7. Machine learning", Order = 2)]
        public int MetaBurnInSamples { get; set; }

        [NinjaScriptProperty]
        [Range(0.3, 0.9)]
        [Display(Name = "Probability threshold", GroupName = "7. Machine learning", Order = 3)]
        public double MetaProbThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(-1.0, 1.0)]
        [Display(Name = "Min Brier skill to activate", GroupName = "7. Machine learning", Order = 4)]
        public double MetaMinSkill { get; set; }

        [NinjaScriptProperty]
        [Range(0.0001, 1.0)]
        [Display(Name = "Learning rate", GroupName = "7. Machine learning", Order = 5)]
        public double MetaLearningRate { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "L2 regularisation", GroupName = "7. Machine learning", Order = 6)]
        public double MetaL2 { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 5.0)]
        [Display(Name = "Label profit barrier (R)", GroupName = "7. Machine learning", Order = 7)]
        public double MetaLabelR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Confidence-scaled sizing", GroupName = "7. Machine learning", Order = 8)]
        public bool MetaConfidenceSizing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Recurring blackouts ET (HH:mm;...)", GroupName = "8. News", Order = 1)]
        public string RecurringBlackoutsEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Event blackouts ET (yyyy-MM-dd HH:mm;...)", Description = "FOMC, CPI, NFP etc. Positions are flattened before these when enabled.", GroupName = "8. News", Order = 2)]
        public string EventBlackoutsEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Minutes before", GroupName = "8. News", Order = 3)]
        public int NewsMinutesBefore { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "Minutes after", GroupName = "8. News", Order = 4)]
        public int NewsMinutesAfter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flatten before dated events", GroupName = "8. News", Order = 5)]
        public bool FlattenBeforeEvents { get; set; }

        [Display(Name = "Write trade journal", GroupName = "9. Diagnostics", Order = 1)]
        public bool EnableJournal { get; set; }

        [Display(Name = "Journal historical trades", GroupName = "9. Diagnostics", Order = 2)]
        public bool JournalHistorical { get; set; }

        [Display(Name = "Show status panel", GroupName = "9. Diagnostics", Order = 3)]
        public bool ShowStatus { get; set; }

        [Display(Name = "Verbose log", GroupName = "9. Diagnostics", Order = 4)]
        public bool VerboseLog { get; set; }

        #endregion
    }
}
