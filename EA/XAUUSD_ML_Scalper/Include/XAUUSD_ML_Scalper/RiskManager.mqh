//+------------------------------------------------------------------+
//| RiskManager.mqh                                                   |
//| Position sizing and account-protection logic. Deliberately        |
//| conservative-by-default: this file has no martingale, no grid,    |
//| no "double down after a loss" logic. On a 100 EUR account with    |
//| 1:500 leverage a losing streak can wipe the account in minutes if |
//| position sizing is not strictly capped - the caps below are hard  |
//| limits, not suggestions.                                          |
//+------------------------------------------------------------------+
#ifndef XAUUSD_ML_SCALPER_RISKMANAGER_MQH
#define XAUUSD_ML_SCALPER_RISKMANAGER_MQH
#property strict
#include "Common.mqh"

//+------------------------------------------------------------------+
//| Risk-based lot size for a given stop-loss distance (in price).    |
//| Caps by: risk %, hard cap %, free-margin usage %, broker limits.  |
//+------------------------------------------------------------------+
double CalcLotSize(double riskPercent, double maxRiskPercentCap, double slDistancePrice,
                    double maxFreeMarginUsagePercent, ENUM_ORDER_TYPE orderType)
{
   if(slDistancePrice <= 0.0) return 0.0;

   riskPercent = ClampD(riskPercent, 0.1, maxRiskPercentCap);

   double balance    = AccountInfoDouble(ACCOUNT_BALANCE);
   double riskAmount = balance * (riskPercent / 100.0);

   double tickValue = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
   double tickSize   = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
   if(tickSize <= 0.0) return 0.0;
   double moneyPerPriceUnitPerLot = tickValue / tickSize;
   if(moneyPerPriceUnitPerLot <= 0.0) return 0.0;

   double lossPerLot = slDistancePrice * moneyPerPriceUnitPerLot;
   if(lossPerLot <= 0.0) return 0.0;

   double lots = riskAmount / lossPerLot;

   double minLot  = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
   double maxLot  = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
   double lotStep = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
   if(lotStep <= 0.0) lotStep = minLot;

   lots = MathFloor(lots / lotStep) * lotStep;
   lots = ClampD(lots, 0.0, maxLot);

   if(lots < minLot)
      return 0.0; // account too small for this risk% / SL distance combo - skip the trade

   // Hard margin-usage cap so a single position can never eat the whole account
   double price = (orderType == ORDER_TYPE_BUY)
                   ? SymbolInfoDouble(_Symbol, SYMBOL_ASK)
                   : SymbolInfoDouble(_Symbol, SYMBOL_BID);
   double marginRequired = 0.0;
   if(OrderCalcMargin(orderType, _Symbol, lots, price, marginRequired))
   {
      double freeMargin   = AccountInfoDouble(ACCOUNT_MARGIN_FREE);
      double marginBudget = freeMargin * (maxFreeMarginUsagePercent / 100.0);
      while(marginRequired > marginBudget && lots > minLot)
      {
         lots -= lotStep;
         lots = ClampD(lots, 0.0, maxLot);
         if(lots < minLot) { lots = 0.0; break; }
         OrderCalcMargin(orderType, _Symbol, lots, price, marginRequired);
      }
   }

   return lots;
}

//+------------------------------------------------------------------+
//| Daily loss limit. Balance is snapshotted at the first tick of    |
//| each new broker day via global variables (persist across         |
//| restarts/recompiles of the EA on the same terminal).             |
//+------------------------------------------------------------------+
void UpdateDailyBalanceSnapshot(string prefix)
{
   string dayKey  = prefix + "_LastDay";
   string balKey  = prefix + "_DayStartBalance";

   MqlDateTime tmNow;
   TimeToStruct(TimeCurrent(), tmNow);
   int todayStamp = tmNow.year * 10000 + tmNow.mon * 100 + tmNow.day;

   int storedDay = (int)GlobalVariableGet(dayKey);
   if(storedDay != todayStamp)
   {
      GlobalVariableSet(dayKey, (double)todayStamp);
      GlobalVariableSet(balKey, AccountInfoDouble(ACCOUNT_BALANCE));
   }
   else if(!GlobalVariableCheck(balKey))
   {
      GlobalVariableSet(balKey, AccountInfoDouble(ACCOUNT_BALANCE));
   }
}

bool IsDailyLossLimitHit(string prefix, double maxDailyLossPercent)
{
   string balKey = prefix + "_DayStartBalance";
   if(!GlobalVariableCheck(balKey)) return false;

   double dayStart = GlobalVariableGet(balKey);
   if(dayStart <= 0.0) return false;

   double equity = AccountInfoDouble(ACCOUNT_EQUITY);
   double lossPercent = (dayStart - equity) / dayStart * 100.0;
   return lossPercent >= maxDailyLossPercent;
}

//+------------------------------------------------------------------+
//| Drawdown kill switch, tracked from the highest equity ever seen  |
//| since the EA was first attached (persisted per symbol+magic).    |
//+------------------------------------------------------------------+
bool IsDrawdownKillSwitchHit(string prefix, double maxDrawdownPercent)
{
   string peakKey = prefix + "_PeakEquity";
   double equity = AccountInfoDouble(ACCOUNT_EQUITY);

   double peak = GlobalVariableCheck(peakKey) ? GlobalVariableGet(peakKey) : equity;
   if(equity > peak)
   {
      peak = equity;
      GlobalVariableSet(peakKey, peak);
   }

   if(peak <= 0.0) return false;
   double ddPercent = (peak - equity) / peak * 100.0;
   return ddPercent >= maxDrawdownPercent;
}

bool IsSpreadAcceptable(int maxSpreadPoints)
{
   long spreadPoints = SymbolInfoInteger(_Symbol, SYMBOL_SPREAD);
   return spreadPoints > 0 && spreadPoints <= maxSpreadPoints;
}

bool IsWithinTradingHours(int startHour, int endHour)
{
   MqlDateTime tm;
   TimeToStruct(TimeCurrent(), tm);
   int h = tm.hour;
   if(startHour == endHour) return true; // filter disabled by config
   if(startHour < endHour)
      return (h >= startHour && h < endHour);
   return (h >= startHour || h < endHour); // wraps past midnight
}

#endif // XAUUSD_ML_SCALPER_RISKMANAGER_MQH
//+------------------------------------------------------------------+
