//+------------------------------------------------------------------+
//|                                          XAUUSD_ML_Scalper.mq5    |
//|                                                                    |
//| ML-assisted XAUUSD (Gold) M1 scalping Expert Advisor.              |
//|                                                                    |
//| ================================================================ |
//| RISK WARNING - READ BEFORE USING                                  |
//| This EA is built around a 100 EUR / 1:500-leverage style small     |
//| account. High leverage does not create an edge - it only lets you |
//| open larger positions relative to your balance, which means       |
//| losses (and margin calls) happen just as fast as gains. There is  |
//| no configuration of this EA, and no machine-learning model, that  |
//| can guarantee profit or "pump up" an account safely. Gold M1      |
//| scalping is one of the hardest styles to trade profitably because |
//| of spread/slippage relative to the average 1-minute move.          |
//|                                                                    |
//| - Demo-test for weeks before any live use.                        |
//| - Never disable the daily-loss / drawdown kill switches.          |
//| - Never raise InpMaxRiskPercentCap beyond a level where a losing   |
//|   streak (10 losses in a row WILL happen) is something you can    |
//|   actually afford to lose.                                        |
//| - The bundled ML model ships UNTRAINED (see MLModel.mqh) and will |
//|   not open any trades until you train it on real history via      |
//|   python/train_model.py.                                          |
//| ================================================================ |
//+------------------------------------------------------------------+
#property copyright "xauusd-ml-scalper-mql5"
#property version   "1.00"
#property strict

#include <Trade/Trade.mqh>
#include <XAUUSD_ML_Scalper/FeatureEngine.mqh>
#include <XAUUSD_ML_Scalper/RiskManager.mqh>

//--- ML settings
input group "=== ML Settings ==="
input double InpConfidenceThreshold   = 0.65;  // min confidence 0..1 (|prob-0.5|*2) required to trade
input int    InpMinBarsBetweenTrades  = 3;      // cooldown, in M1 bars, after any trade

//--- Risk management (this is what keeps a 100 EUR account alive)
input group "=== Risk Management ==="
input double InpRiskPercentPerTrade   = 1.5;    // % balance risked per trade
input double InpMaxRiskPercentCap     = 3.0;    // absolute hard cap, even in aggressive mode
input double InpMaxDailyLossPercent   = 6.0;    // stop trading for the rest of the day
input double InpMaxDrawdownPercent    = 20.0;   // kill switch: close all + halt EA
input double InpMaxFreeMarginUsage    = 40.0;   // max % of free margin one position may use
input int    InpMaxOpenPositions      = 1;
input bool   InpAggressiveCompounding = false;  // scale risk% with equity growth, still capped

//--- Trade parameters
input group "=== Trade Parameters ==="
input double InpATR_SL_Multiplier     = 1.5;
input double InpATR_TP_Multiplier     = 2.5;
input bool   InpUseTrailingStop       = true;
input double InpTrailingATRMultiplier = 1.0;
input int    InpMaxSpreadPoints       = 350;    // gold spreads are wide; tune per broker
input ulong  InpMagicNumber           = 20260710;

//--- Session filter (broker server time)
input group "=== Session Filter ==="
input bool InpUseSessionFilter  = true;
input int  InpSessionStartHour  = 7;
input int  InpSessionEndHour    = 20;

//--- Indicator periods
input group "=== Indicators ==="
input int    InpRSIPeriod       = 14;
input int    InpMACDFast        = 12;
input int    InpMACDSlow        = 26;
input int    InpMACDSignal      = 9;
input int    InpBBPeriod        = 20;
input double InpBBDeviation     = 2.0;
input int    InpATRPeriod       = 14;
input int    InpStochK          = 14;
input int    InpStochD          = 3;
input int    InpStochSlowing    = 3;
input int    InpEMAFast         = 8;
input int    InpEMASlow         = 21;
input int    InpEMA200          = 200;

CTrade trade;
MLIndicatorHandles h;
string g_Prefix;
datetime g_LastBarTime = 0;
int      g_BarsSinceTrade = 1000;
bool     g_TradingHalted = false;

//+------------------------------------------------------------------+
int OnInit()
{
   if(StringFind(_Symbol, "XAU") < 0)
      Print("WARNING: chart symbol '", _Symbol, "' does not look like XAUUSD. ",
            "This EA's feature scaling was designed for gold - verify before trading.");

   h.rsi     = iRSI(_Symbol, PERIOD_M1, InpRSIPeriod, PRICE_CLOSE);
   h.macd    = iMACD(_Symbol, PERIOD_M1, InpMACDFast, InpMACDSlow, InpMACDSignal, PRICE_CLOSE);
   h.bb      = iBands(_Symbol, PERIOD_M1, InpBBPeriod, 0, InpBBDeviation, PRICE_CLOSE);
   h.atr     = iATR(_Symbol, PERIOD_M1, InpATRPeriod);
   h.stoch   = iStochastic(_Symbol, PERIOD_M1, InpStochK, InpStochD, InpStochSlowing, MODE_SMA, STO_LOWHIGH);
   h.emaFast = iMA(_Symbol, PERIOD_M1, InpEMAFast, 0, MODE_EMA, PRICE_CLOSE);
   h.emaSlow = iMA(_Symbol, PERIOD_M1, InpEMASlow, 0, MODE_EMA, PRICE_CLOSE);
   h.ema200  = iMA(_Symbol, PERIOD_M1, InpEMA200, 0, MODE_EMA, PRICE_CLOSE);

   if(h.rsi == INVALID_HANDLE || h.macd == INVALID_HANDLE || h.bb == INVALID_HANDLE ||
      h.atr == INVALID_HANDLE || h.stoch == INVALID_HANDLE || h.emaFast == INVALID_HANDLE ||
      h.emaSlow == INVALID_HANDLE || h.ema200 == INVALID_HANDLE)
   {
      Print("Failed to create one or more indicator handles.");
      return INIT_FAILED;
   }

   trade.SetExpertMagicNumber((long)InpMagicNumber);
   trade.SetTypeFillingBySymbol(_Symbol);
   trade.SetDeviationInPoints(50);

   g_Prefix = "MLScalp_" + _Symbol + "_" + IntegerToString(InpMagicNumber);
   UpdateDailyBalanceSnapshot(g_Prefix);

   Print("XAUUSD_ML_Scalper initialised. Balance=", AccountInfoDouble(ACCOUNT_BALANCE),
         " Leverage=1:", AccountInfoInteger(ACCOUNT_LEVERAGE));
   return INIT_SUCCEEDED;
}

//+------------------------------------------------------------------+
void OnDeinit(const int reason)
{
   IndicatorRelease(h.rsi);
   IndicatorRelease(h.macd);
   IndicatorRelease(h.bb);
   IndicatorRelease(h.atr);
   IndicatorRelease(h.stoch);
   IndicatorRelease(h.emaFast);
   IndicatorRelease(h.emaSlow);
   IndicatorRelease(h.ema200);
}

//+------------------------------------------------------------------+
int CountOwnPositions()
{
   int count = 0;
   for(int i = 0; i < PositionsTotal(); i++)
   {
      ulong ticket = PositionGetTicket(i);
      if(ticket == 0) continue;
      if(PositionGetString(POSITION_SYMBOL) == _Symbol &&
         PositionGetInteger(POSITION_MAGIC) == (long)InpMagicNumber)
         count++;
   }
   return count;
}

void CloseAllOwnPositions()
{
   for(int i = PositionsTotal() - 1; i >= 0; i--)
   {
      ulong ticket = PositionGetTicket(i);
      if(ticket == 0) continue;
      if(PositionGetString(POSITION_SYMBOL) == _Symbol &&
         PositionGetInteger(POSITION_MAGIC) == (long)InpMagicNumber)
         trade.PositionClose(ticket);
   }
}

//+------------------------------------------------------------------+
//| ATR-based trailing stop for any open position of this EA          |
//+------------------------------------------------------------------+
void ManageOpenPositions()
{
   if(!InpUseTrailingStop) return;

   double atrBuf[];
   if(CopyBuffer(h.atr, 0, 1, 1, atrBuf) < 1) return;
   double atr = atrBuf[0];
   if(atr <= 0.0) return;

   double trailDist = atr * InpTrailingATRMultiplier;

   for(int i = 0; i < PositionsTotal(); i++)
   {
      ulong ticket = PositionGetTicket(i);
      if(ticket == 0) continue;
      if(PositionGetString(POSITION_SYMBOL) != _Symbol) continue;
      if(PositionGetInteger(POSITION_MAGIC) != (long)InpMagicNumber) continue;

      long   type   = PositionGetInteger(POSITION_TYPE);
      double curSL  = PositionGetDouble(POSITION_SL);
      double curTP  = PositionGetDouble(POSITION_TP);

      if(type == POSITION_TYPE_BUY)
      {
         double bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);
         double newSL = bid - trailDist;
         if(newSL > curSL && (curSL == 0 || newSL - curSL > _Point))
            trade.PositionModify(ticket, NormalizeDouble(newSL, _Digits), curTP);
      }
      else if(type == POSITION_TYPE_SELL)
      {
         double ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
         double newSL = ask + trailDist;
         if((curSL == 0 || newSL < curSL) && (curSL == 0 || curSL - newSL > _Point))
            trade.PositionModify(ticket, NormalizeDouble(newSL, _Digits), curTP);
      }
   }
}

//+------------------------------------------------------------------+
double EffectiveRiskPercent()
{
   if(!InpAggressiveCompounding)
      return ClampD(InpRiskPercentPerTrade, 0.1, InpMaxRiskPercentCap);

   // Scale risk with equity growth relative to the very first balance snapshot,
   // still hard-capped by InpMaxRiskPercentCap. This lets risk in absolute money
   // terms grow as the account grows, without ever exceeding the configured cap.
   double growthFactor = 1.0;
   string startKey = g_Prefix + "_InitialBalance";
   if(!GlobalVariableCheck(startKey))
      GlobalVariableSet(startKey, AccountInfoDouble(ACCOUNT_BALANCE));
   double initialBalance = GlobalVariableGet(startKey);
   if(initialBalance > 0.0)
      growthFactor = AccountInfoDouble(ACCOUNT_BALANCE) / initialBalance;

   double scaled = InpRiskPercentPerTrade * MathSqrt(ClampD(growthFactor, 0.5, 9.0));
   return ClampD(scaled, 0.1, InpMaxRiskPercentCap);
}

//+------------------------------------------------------------------+
void TryOpenTrade()
{
   if(CountOwnPositions() >= InpMaxOpenPositions) return;
   if(g_BarsSinceTrade < InpMinBarsBetweenTrades) return;
   if(!IsSpreadAcceptable(InpMaxSpreadPoints)) return;
   if(InpUseSessionFilter && !IsWithinTradingHours(InpSessionStartHour, InpSessionEndHour)) return;

   double features[];
   if(!BuildFeatureVector(h, features)) return;

   double prob = MLPredict(features);
   double confidence = MathAbs(prob - 0.5) * 2.0;
   if(confidence < InpConfidenceThreshold) return;

   double atrBuf[];
   if(CopyBuffer(h.atr, 0, 1, 1, atrBuf) < 1) return;
   double atr = atrBuf[0];
   if(atr <= 0.0) return;

   double slDist = atr * InpATR_SL_Multiplier;
   double tpDist = atr * InpATR_TP_Multiplier;

   // Respect the broker's minimum stop distance so the order isn't rejected
   long stopsLevelPoints = SymbolInfoInteger(_Symbol, SYMBOL_TRADE_STOPS_LEVEL);
   double minStopDist = (double)stopsLevelPoints * _Point;
   if(minStopDist > 0.0)
   {
      slDist = MathMax(slDist, minStopDist * 1.1);
      tpDist = MathMax(tpDist, minStopDist * 1.1);
   }

   double riskPercent = EffectiveRiskPercent();

   bool isLong = (prob > 0.5);
   ENUM_ORDER_TYPE orderType = isLong ? ORDER_TYPE_BUY : ORDER_TYPE_SELL;

   double lots = CalcLotSize(riskPercent, InpMaxRiskPercentCap, slDist, InpMaxFreeMarginUsage, orderType);
   if(lots <= 0.0)
   {
      Print("Signal fired (prob=", DoubleToString(prob, 3), ") but position size rounds to 0 - ",
            "account balance too small for current risk% / SL distance. Skipping trade.");
      return;
   }

   double price = isLong ? SymbolInfoDouble(_Symbol, SYMBOL_ASK) : SymbolInfoDouble(_Symbol, SYMBOL_BID);
   double sl = isLong ? price - slDist : price + slDist;
   double tp = isLong ? price + tpDist : price - tpDist;

   sl = NormalizeDouble(sl, _Digits);
   tp = NormalizeDouble(tp, _Digits);

   string comment = StringFormat("MLScalp p=%.2f", prob);
   bool ok = isLong
             ? trade.Buy(lots, _Symbol, price, sl, tp, comment)
             : trade.Sell(lots, _Symbol, price, sl, tp, comment);

   if(ok)
      g_BarsSinceTrade = 0;
   else
      Print("Order failed: ", trade.ResultRetcodeDescription());
}

//+------------------------------------------------------------------+
void OnTick()
{
   datetime curBarTime = iTime(_Symbol, PERIOD_M1, 0);
   if(curBarTime == g_LastBarTime)
   {
      ManageOpenPositions();
      return;
   }
   g_LastBarTime = curBarTime;
   g_BarsSinceTrade++;

   UpdateDailyBalanceSnapshot(g_Prefix);

   if(IsDrawdownKillSwitchHit(g_Prefix, InpMaxDrawdownPercent))
   {
      if(!g_TradingHalted)
      {
         Print("DRAWDOWN KILL SWITCH HIT (>=", InpMaxDrawdownPercent,
               "% from peak equity). Closing all positions and halting new trades.");
         CloseAllOwnPositions();
         g_TradingHalted = true;
      }
      return;
   }

   if(IsDailyLossLimitHit(g_Prefix, InpMaxDailyLossPercent))
   {
      ManageOpenPositions();
      return; // no new trades for the rest of the day, but keep managing existing ones
   }

   if(g_TradingHalted)
      return; // stays halted until the EA is removed/re-attached (manual reset by design)

   ManageOpenPositions();
   TryOpenTrade();
}
//+------------------------------------------------------------------+
