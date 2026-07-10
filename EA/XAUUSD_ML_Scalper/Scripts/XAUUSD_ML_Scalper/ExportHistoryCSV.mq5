//+------------------------------------------------------------------+
//| ExportHistoryCSV.mq5                                              |
//| Exports M1 OHLCV history for the current chart symbol to          |
//| MQL5/Files/<symbol>_M1_history.csv so it can be used by            |
//| python/train_model.py.                                             |
//|                                                                    |
//| Usage: drag onto an XAUUSD chart, set InpBars to how many M1       |
//| candles you want (more history = better training, but make sure   |
//| your broker actually keeps that much M1 history available).       |
//+------------------------------------------------------------------+
#property copyright "xauusd-ml-scalper-mql5"
#property version   "1.00"
#property script_show_inputs

input int InpBars = 200000; // number of M1 bars to export (from most recent backwards)

void OnStart()
{
   MqlRates rates[];
   ArraySetAsSeries(rates, true);
   int copied = CopyRates(_Symbol, PERIOD_M1, 0, InpBars, rates);
   if(copied <= 0)
   {
      Print("CopyRates failed, error=", GetLastError());
      return;
   }

   string filename = _Symbol + "_M1_history.csv";
   int fh = FileOpen(filename, FILE_WRITE | FILE_CSV | FILE_ANSI, ',');
   if(fh == INVALID_HANDLE)
   {
      Print("Failed to open ", filename, " for writing, error=", GetLastError());
      return;
   }

   FileWrite(fh, "time", "open", "high", "low", "close", "tick_volume");

   // write oldest -> newest, which is what pandas/training expects
   for(int i = copied - 1; i >= 0; i--)
   {
      FileWrite(fh,
                 TimeToString(rates[i].time, TIME_DATE | TIME_SECONDS),
                 DoubleToString(rates[i].open, _Digits),
                 DoubleToString(rates[i].high, _Digits),
                 DoubleToString(rates[i].low, _Digits),
                 DoubleToString(rates[i].close, _Digits),
                 IntegerToString(rates[i].tick_volume));
   }

   FileClose(fh);
   Print("Exported ", copied, " bars to MQL5/Files/", filename);
}
//+------------------------------------------------------------------+
