//+------------------------------------------------------------------+
//| MLModel.mqh                                                      |
//| Small feed-forward neural net (12 -> 8 -> 4 -> 1) evaluated      |
//| natively in MQL5, no external DLL / Python process required at   |
//| runtime.                                                          |
//|                                                                    |
//| The weights below are an UNTRAINED PLACEHOLDER (all zeros), which |
//| makes MLPredict() always return exactly 0.5 (neutral). This is    |
//| intentional: the EA will not take any trade until you replace this|
//| file with weights produced by python/train_model.py on real       |
//| history. Never trade live with the placeholder weights.           |
//+------------------------------------------------------------------+
#ifndef XAUUSD_ML_SCALPER_MLMODEL_MQH
#define XAUUSD_ML_SCALPER_MLMODEL_MQH
#property strict

#define ML_INPUT_SIZE 12
#define ML_H1_SIZE     8
#define ML_H2_SIZE     4

// --- Layer 1: input -> hidden1 -----------------------------------
double g_ML_W1[ML_INPUT_SIZE][ML_H1_SIZE] =
{
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0},
   {0,0,0,0,0,0,0,0}
};
double g_ML_B1[ML_H1_SIZE] = {0,0,0,0,0,0,0,0};

// --- Layer 2: hidden1 -> hidden2 -----------------------------------
double g_ML_W2[ML_H1_SIZE][ML_H2_SIZE] =
{
   {0,0,0,0},
   {0,0,0,0},
   {0,0,0,0},
   {0,0,0,0},
   {0,0,0,0},
   {0,0,0,0},
   {0,0,0,0},
   {0,0,0,0}
};
double g_ML_B2[ML_H2_SIZE] = {0,0,0,0};

// --- Layer 3: hidden2 -> output (sigmoid) --------------------------
double g_ML_W3[ML_H2_SIZE][1] =
{
   {0},
   {0},
   {0},
   {0}
};
double g_ML_B3[1] = {0};

//+------------------------------------------------------------------+
//| Forward pass. features[] must have ML_INPUT_SIZE elements,       |
//| pre-normalised by FeatureEngine.mqh. Returns P(price up) in 0..1 |
//+------------------------------------------------------------------+
double MLPredict(const double &features[])
{
   if(ArraySize(features) != ML_INPUT_SIZE)
      return 0.5; // fail safe: neutral

   double h1[ML_H1_SIZE];
   for(int j = 0; j < ML_H1_SIZE; j++)
   {
      double sum = g_ML_B1[j];
      for(int i = 0; i < ML_INPUT_SIZE; i++)
         sum += features[i] * g_ML_W1[i][j];
      h1[j] = MathTanh(sum);
   }

   double h2[ML_H2_SIZE];
   for(int j = 0; j < ML_H2_SIZE; j++)
   {
      double sum = g_ML_B2[j];
      for(int i = 0; i < ML_H1_SIZE; i++)
         sum += h1[i] * g_ML_W2[i][j];
      h2[j] = MathTanh(sum);
   }

   double outSum = g_ML_B3[0];
   for(int i = 0; i < ML_H2_SIZE; i++)
      outSum += h2[i] * g_ML_W3[i][0];

   double prob = 1.0 / (1.0 + MathExp(-outSum));
   return prob;
}

#endif // XAUUSD_ML_SCALPER_MLMODEL_MQH
//+------------------------------------------------------------------+
