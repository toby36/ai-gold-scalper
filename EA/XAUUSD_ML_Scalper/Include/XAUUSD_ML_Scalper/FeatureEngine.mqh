//+------------------------------------------------------------------+
//| FeatureEngine.mqh                                                 |
//| Builds the 12-feature vector consumed by MLModel.mqh.             |
//|                                                                    |
//| IMPORTANT: these formulas must stay in lock-step with the         |
//| feature engineering in python/train_model.py. If you change one   |
//| side you MUST change the other and retrain, otherwise the model   |
//| will be fed data it never saw in training.                        |
//|                                                                    |
//| All features are built from the last CLOSED bar (shift = 1) so    |
//| there is no look-ahead / repaint on the still-forming candle.     |
//+------------------------------------------------------------------+
#ifndef XAUUSD_ML_SCALPER_FEATUREENGINE_MQH
#define XAUUSD_ML_SCALPER_FEATUREENGINE_MQH
#property strict
#include "Common.mqh"
#include "MLModel.mqh"

struct MLIndicatorHandles
{
   int rsi;
   int macd;
   int bb;
   int atr;
   int stoch;
   int emaFast;
   int emaSlow;
   int ema200;
};

//+------------------------------------------------------------------+
//| Fills out[] with ML_INPUT_SIZE normalised features.               |
//| Returns false if not enough history / indicator data yet.         |
//+------------------------------------------------------------------+
bool BuildFeatureVector(const MLIndicatorHandles &h, double &out[])
{
   ArrayResize(out, ML_INPUT_SIZE);

   double rsiBuf[], macdMain[], macdSignal[], bbUpper[], bbLower[], atrBuf[],
          stochMain[], stochSignal[], emaFastBuf[], emaSlowBuf[], ema200Buf[];

   if(CopyBuffer(h.rsi, 0, 1, 1, rsiBuf) < 1) return false;
   if(CopyBuffer(h.macd, 0, 1, 1, macdMain) < 1) return false;
   if(CopyBuffer(h.macd, 1, 1, 1, macdSignal) < 1) return false;
   if(CopyBuffer(h.bb, 1, 1, 1, bbUpper) < 1) return false; // upper band buffer index 1
   if(CopyBuffer(h.bb, 2, 1, 1, bbLower) < 1) return false; // lower band buffer index 2
   if(CopyBuffer(h.atr, 0, 1, 1, atrBuf) < 1) return false;
   if(CopyBuffer(h.stoch, 0, 1, 1, stochMain) < 1) return false;
   if(CopyBuffer(h.stoch, 1, 1, 1, stochSignal) < 1) return false;
   if(CopyBuffer(h.emaFast, 0, 1, 1, emaFastBuf) < 1) return false;
   if(CopyBuffer(h.emaSlow, 0, 1, 1, emaSlowBuf) < 1) return false;
   if(CopyBuffer(h.ema200, 0, 1, 1, ema200Buf) < 1) return false;

   MqlRates rates[];
   ArraySetAsSeries(rates, true);
   int copied = CopyRates(_Symbol, PERIOD_M1, 1, 25, rates);
   if(copied < 25) return false;

   double atr        = atrBuf[0];
   if(atr <= 0.0) return false;
   double macdMainV   = macdMain[0];
   double macdSignalV = macdSignal[0];
   double macdHist     = macdMainV - macdSignalV;
   double close1       = rates[0].close; // last closed bar
   double close11      = rates[10].close; // 10 bars before that

   double sumVol = 0.0;
   for(int i = 0; i < 20; i++)
      sumVol += (double)rates[i].tick_volume;
   double avgVol20 = sumVol / 20.0;
   double vol1     = (double)rates[0].tick_volume;

   double pctB = (bbUpper[0] - bbLower[0] > 0.0)
                 ? (close1 - bbLower[0]) / (bbUpper[0] - bbLower[0])
                 : 0.5;

   out[0]  = (rsiBuf[0] / 100.0 - 0.5) * 2.0;                              // RSI, -1..1
   out[1]  = ClampD(macdMainV / atr, -3.0, 3.0) / 3.0;                     // MACD main / ATR
   out[2]  = ClampD(macdSignalV / atr, -3.0, 3.0) / 3.0;                   // MACD signal / ATR
   out[3]  = ClampD(macdHist / atr, -3.0, 3.0) / 3.0;                      // MACD hist / ATR
   out[4]  = ClampD(pctB, 0.0, 1.0) * 2.0 - 1.0;                           // Bollinger %B, -1..1
   out[5]  = ClampD((atr / close1) * 1000.0, 0.0, 5.0) / 5.0;              // volatility ratio, 0..1
   out[6]  = stochMain[0] / 100.0;                                        // Stoch %K, 0..1
   out[7]  = stochSignal[0] / 100.0;                                      // Stoch %D, 0..1
   out[8]  = ClampD((close1 - close11) / atr, -5.0, 5.0) / 5.0;           // 10-bar momentum / ATR
   out[9]  = ClampD((emaFastBuf[0] - emaSlowBuf[0]) / atr, -5.0, 5.0) / 5.0; // trend strength
   out[10] = ClampD((close1 - ema200Buf[0]) / atr, -8.0, 8.0) / 8.0;      // position vs long trend
   out[11] = (avgVol20 > 0.0) ? ClampD(vol1 / avgVol20, 0.0, 3.0) / 3.0 : 0.0; // volume surge

   return true;
}

#endif // XAUUSD_ML_SCALPER_FEATUREENGINE_MQH
//+------------------------------------------------------------------+
