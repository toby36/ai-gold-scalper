//+------------------------------------------------------------------+
//| Common.mqh - shared helpers                                       |
//+------------------------------------------------------------------+
#ifndef XAUUSD_ML_SCALPER_COMMON_MQH
#define XAUUSD_ML_SCALPER_COMMON_MQH
#property strict

double ClampD(double v, double lo, double hi)
{
   if(v < lo) return lo;
   if(v > hi) return hi;
   return v;
}

#endif // XAUUSD_ML_SCALPER_COMMON_MQH
