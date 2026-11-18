# FlexChallengeBot – NinjaTrader 8 intraday bot a FundedNext Futures Flex 50K challenge-hez

Teljes, éles kereskedésre szánt NinjaScript stratégia (C#, NinjaTrader 8). Két, publikált kutatásokon alapuló
intraday momentum modult, egy online tanuló ML meta-szűrőt és egy kifejezetten prop-firm szabályokra épített
kockázat- és megbízáskezelő motort tartalmaz.

| Fájl | Tartalom |
|---|---|
| `FlexChallengeBot.cs` | A stratégia: adatfeldolgozás, jelzések, megbízáskezelés (unmanaged), kockázati őr, státuszpanel |
| `FlexBotCore.cs` | Platformfüggetlen mag: challenge-állapotgép, pozícióméretezés, ATR, perc-profilok, online logisztikus regresszió, triple-barrier címkézés, hírnaptár, állapotmentés, napló |

---

## 1. A challenge szabályai, amelyekre a bot épül

FundedNext Futures **Flex 50K** (forrás: FundedNext hivatalos oldalai és független összefoglalók, 2026):

| Szabály | Érték | Hogyan kezeli a bot |
|---|---|---|
| Profitcél | $2 500 | `ProfitTargetUsd`; elérésekor zárás és végleges leállás |
| Max loss | $1 500, **EOD trailing** | Küszöb = max(EOD egyenleg) − 1 500, napközben a nyitott P/L-lel együtt figyelve |
| Küszöb rögzülése | kezdő egyenleg + $100 (50 100) | `ThresholdLockOffsetUsd` |
| Napi veszteséglimit | nincs | A bot saját limitet tart (`DailyLossLimitUsd` = 450), hogy egy rossz nap ne vigye el a teljes keretet |
| Konzisztencia | legjobb nap ≤ a cél 40%-a, különben nő a cél | `DailyProfitCapUsd` = 900 (< 1 000) → a cél nem nő meg; az effektív célt a bot a legjobb nap alapján számolja |
| Kontraktuslimit | 3 mini / 30 micro | Automatikus felismerés a műszer gyökeréből (MNQ, MES… = micro) |
| Overnight / hétvége | tilos, zárás 15:10 CT (16:10 ET) előtt | `FlattenTimeEt` = 15:50 ET, plusz ünnepnapi korai zárás a trading hours sablonból, plusz NT exit-on-session-close tartalék |
| Automatizált kereskedés | engedélyezett, HFT és order flooding tilos | Max. 4 trade/nap, a stopok csak bar-záráskor módosulnak, nincs tick-scalping |
| Hírkereskedés | engedélyezett | Mégis van opcionális tiltási ablak, mert a hírek körüli slippage a $1 500-as keretet veszélyezteti |

> A szabályok változhatnak. Indítás előtt nézd meg az aktuális értékeket a FundedNext oldalán, és szükség
> esetén írd át a paramétereket. A bot minden challenge-értéket paraméterként kezel.

**A leggyorsabb elvi teljesítés:** a konzisztencia-szabály miatt egy nap legfeljebb ~$1 000-t hozhat úgy, hogy a cél
ne nőjön, így a minimum **3 nyerő nap** (pl. 900 + 900 + 700). A bot ezt a profilt célozza: nagy napi profitlehetőség,
de a plafon elérésekor azonnal zár és aznapra leáll.

---

## 2. Kutatás: amit nyílt forráskódú rendszerekben találtam

Átnéztem a GitHubon legtöbb csillagot kapott NinjaTrader 8 stratégiákat (`khanh1030/KCStrategies` – KCAlgoBase család,
`pjsgsy/pjsStrategyBase` – unmanaged sablon, `ayb/ninjatrader-automated-trading-strategy`,
`marksantiago290/OrderFlowBot`, `MicroTrendsLtd/NinjaTrader8`), a NinjaTrader fórum ORB-szálait, valamint a
nyilvános TradingView ORB/VWAP scriptek logikáját.

### Jó gyakorlatok, amelyeket átvettem
* Unmanaged order handling teljes OCO-kontrollal (pjsStrategyBase).
* `RealtimeErrorHandling` és `StartBehavior.WaitUntilFlat` tudatos használata, `GetRealtimeOrder` átváltáskor.
* Napi P/L-limitek, trailing drawdown figyelés, kereskedési időablakok, napló CSV-be (KCAlgoBase).
* ORB + VWAP szűrő + napi maximum trade (TradingView ORB scriptek).

### Hibák, amelyeket a meglévő rendszerekben találtam, és a javításuk itt

| # | Hiba a nyílt forrású kódban | Következmény | Megoldás ebben a botban |
|---|---|---|---|
| 1 | A trailing drawdown **napközbeni realizált** P/L-en fut, és ha visszajön a profit, a kód **újraengedélyezi** a kereskedést (KCAlgoBase) | Prop-firmnél a sértés végleges; a modell rossz küszöböt követ | Valódi EOD trailing állapotgép rögzülési szinttel; a sértés végleges, napközben a nyitott P/L-t is figyeli |
| 2 | Drawdown-ellenőrzés csak trade-záráskor, a nyitott pozíció lebegő veszteségét figyelmen kívül hagyja | A számla úgy sérülhet, hogy a bot nem is tud róla | Minden árváltozáskor (backtestben a bar szélső értékével) számolt equity-őr |
| 3 | Pozícióméret `NetLiquidation × %` alapján | Egy 50K-s prop számlán a valódi kockázati keret nem 50 000, hanem a küszöbig hátralévő ~1 500 | A méret a küszöbig hátralévő távolság, a napi keret, a konzisztencia-plafon, a célig hátralévő összeg és a kontraktuslimit minimuma; slippage és jutalék is benne van |
| 4 | A konzisztencia-szabályt egyik rendszer sem kezeli | Egy túl jó nap megemeli a célt, és lelassítja a teljesítést | Napi profitplafon, effektív cél számítása, és a méret úgy korlátozva, hogy 1R nyereség még beférjen a plafon alá |
| 5 | A bracketek az entry `Filled` állapotára jönnek létre, a **teljes** mennyiséggel; részteljesülést nem kezelnek (pjsStrategyBase) | Részteljesülésnél védtelen vagy túlméretezett stop | A stopok minden entry-végrehajtásra létrejönnek vagy átméreteződnek a ténylegesen teljesült mennyiségre |
| 6 | Egy stop + több target **nem OCO**-ban, vagy OCO-ban úgy, hogy a target teljesülése a stopot is törli | Védtelen maradék pozíció vagy overfill | Két független OCO-láb (scalp és runner), mindegyik saját stoppal; overfill-észlelés és automatikus korrekció |
| 7 | `Account.Orders` végigiterálása és **minden** munkában lévő megbízás törlése név alapján (pjsStrategyBase) | Más műszerek vagy kézi megbízások törlése | Csak a saját, trade-enként egyedi nevű megbízásait kezeli |
| 8 | Flatten esetén a market exit **azonnal** megy, miközben a stopok még élnek | Kettős teljesülés, ellenirányú pozíció | Cancel-then-flatten watchdoggal (élesben 2 s), plusz overfill-korrekció |
| 9 | Elutasított stop/target esetén `StopCancelClose` → a stratégia leáll, a pozíció kezeletlen marad | Kontroll nélküli pozíció | `IgnoreAllErrors` + saját kezelés: elutasított védőmegbízás esetén azonnali zárás, zárómegbízás újrapróbálása, riasztás |
| 10 | A „dinamikus tanulás” a lezárt trade-ek MFE/MAE átlagához igazítja a TP/SL-t (KCAlgoBase) | Túlillesztés, a kiválasztott trade-ek torzított mintáján tanul | Meta-labeling **minden** jelzésen (a kiszűrteken is) triple-barrier címkével; csak akkor aktív, ha out-of-sample Brier-skillje pozitív |
| 11 | Fix helyi idő (`ToTime(Time[0])`) a kereskedési ablakokhoz | Hibás ablakok más időzónájú gépen és DST-váltáskor | Minden időpont explicit ET-re konvertálva; a kereskedési nap határa 18:00 ET (CME) |
| 12 | Ünnepnapi korai zárás figyelmen kívül hagyása | Overnight pozíció, prop-szabálysértés | `SessionIterator.ActualSessionEnd` alapján lezárás 5 perccel a tényleges zárás előtt |
| 13 | Újraindítás után elveszik a napi P/L, a legjobb nap és az EOD csúcs | Újraindítás után rossz limitek | Az élő állapot fájlba mentődik (atomikus csere), és induláskor visszatöltődik |

---

## 3. Stratégialogika

### Miért intraday momentum, és miért nem tick-scalping?
* A Flex szabályai (EOD trailing, nincs napi limit, 40% konzisztencia) olyan stratégiának kedveznek, amelynek
  **napi szinten kontrollált a szórása**, és akár egy-két jó trade-del el tudja érni a napi plafont.
* A tick-szintű scalping NQ/ES-en jutalék és slippage után jellemzően negatív várható értékű, ráadásul a firm HFT-
  és order-flooding-tilalmába ütközhet.
* Két publikáltan robusztus intraday hatást használok:
  1. **Opening Range Breakout** – Zarattini & Aziz (2023), *Can Day Trading Really Be Profitable?*: az 5 perces
     nyitó tartomány kitörése az irányt jelző napokon.
  2. **Noise Area intraday momentum** – Zarattini, Aziz & Barbon (2024), *Beat the Market: An Effective Intraday
     Momentum Strategy for S&P500 ETF (SPY)*: a „zajzóna” = az elmúlt 14 nap átlagos |close/open − 1| mozgása perc-
     pontosan; ha az ár félórás ellenőrzőponton kilép belőle, momentum-belépés, és kilépés, amikor visszaesik a
     max(határ, VWAP) alá.

### Belépések
**ORB** (alapból 09:30–09:35 ET tartomány, belépés 11:30-ig):
* 1 perces záróár az OR fölött vagy alatt (naponta irányonként csak az első kitörés számít – nincs utánafutás),
* OR-szélesség a napi (RTH) ATR 4–40%-a között,
* a kitörés nem nyúlt túl (≤ 0,6 × OR-szélesség),
* az ár a VWAP megfelelő oldalán van,
* relatív kumulált volumen ≥ 0,8 (a napon „van élet”),
* stop: az OR ellenoldala (`OrbStopFraction` = 1), min. 16 tick.

**Noise Area** (ellenőrzőpontok 10:00-tól félóránként):
* záróár a felső határ és a VWAP fölött (long), vagy az alsó határ és a VWAP alatt (short),
* a határok gap-korrekcióval: `max(open, előző close) × (1 + σ_perc)` és `min(open, előző close) × (1 − σ_perc)`,
* kemény stop: max(határ, VWAP) − 1,5 × 1 perces ATR.

### Kilépések
* **Scalp-láb** (a pozíció 50%-a): target 1R. Teljesülése után a runner stopja breakeven + 2 tickre kerül.
* **Runner-láb**: target 3R, trailing stop (ORB: 3 × 1 perces ATR chandelier, illetve a VWAP, ha az kedvezőbb;
  Noise: a zajhatár/VWAP referencia), és csak kedvező irányba mozog.
* Noise modulnál ellenőrzőponti kilépés, ha a záróár visszaesik a referencia alá.
* Időalapú kilépés: max. 240 perc tartás, 15:50 ET-kor kötelező zárás, korai zárásnál a session vége előtt 5 perccel.
* Opcionálisan zárás a megadott dátumú, nagy hatású hírek előtt.

> Egy kontraktusnál a teljes pozíció a scalp-lábba kerül, így az 1R-es trade-ek egyenletesebb napi eloszlást adnak.

### ML: meta-labeling (indokolt, és ezért van benne)
* **Elsődleges modell** = a fenti szabályalapú jelzések; **másodlagos modell** = online logisztikus regresszió, amely
  azt becsli, hogy egy jelzés eléri-e a +1R-t a −1R stop előtt (López de Prado: meta-labeling, triple-barrier).
* 13 jellemző: modul, irány, OR-szélesség/ATR, VWAP-távolság/ATR, log relatív volumen, napszak,
  intraday/napi volatilitás aránya, gap, előző napi irány, Kaufman-hatékonysági arány, napi elmozdulás,
  stopszélesség/ATR, OR-gyertya iránya. Minden jellemző irányra előjelezett.
* Minden jelzés címkét kap, a kiszűrtek is („árnyék-trade”), ezért a tanítóminta nem torzított, és a modell
  akkor sem éhezik ki, ha sokat szűr.
* **Prequential értékelés:** minden mintára előbb jósol, csak utána tanul belőle, így a Brier-score valódi
  out-of-sample mérőszám. A szűrő csak akkor kapcsol be, ha legalább 40 címkézett minta van, **és** a
  Brier-skill a bázisrátás előrejelzőhöz képest pozitív. Haszontalan modell tehát nem tud ártani.
* Bizalom-arányos méretezés: a valószínűség 50–100% közötti méretszorzót ad.
* A modell a historikus „bemelegítés” alatt tanul, és élesben folytatja a tanulást, így minden indítás
  determinisztikus.

**Miért nincs CNN/DL/RL?** Évente nagyságrendileg 250 kereskedési nap és napi 1–3 jelzés van, így néhány száz
címkézett minta keletkezik, ami egy mély hálónak vagy RL-ágensnek kevés. Ekkora mintán ezek a modellek szinte
biztosan túlillesztenek, nem-stacionárius piacon instabilak, a NinjaTrader 8 .NET Framework környezetében külső
futtatókörnyezetet (Python/ONNX) igényelnének, és a késleltetés, illetve a hibaforrások száma is nőne. Egy jól
regularizált, online tanuló lineáris meta-modell, amely objektíven méri a saját tudását, ekkora adaton a
szakmailag helyes választás.

### Kockázatkezelés (az egész rendszer magja)
Egy trade kockázati kerete (USD) a következők **minimuma**:
1. `RiskPerTradeUsd` (250),
2. (egyenleg − küszöb − puffer) × 0,30 – ahogy közeledik a küszöb, automatikusan csökken a méret,
3. a napi veszteségkeretből hátralévő rész.

A kontraktusszám ebből úgy jön ki, hogy a stopot, a slippage-et és a jutalékot is fedezze, majd további korlátok:
a konzisztencia-plafon alatt beférjen egy 1R nyereség, a célig hátralévő összeghez illeszkedjen, és ne lépje át a
kontraktuslimitet.

**Tick-szintű őr**, amely minden árváltozáskor ellenőriz, és bármelyik feltételnél azonnal zár:
* a küszöb sérülése (végleges leállás),
* a küszöb + $150 puffer elérése (napi leállás),
* a −$450 napi veszteség (napi leállás),
* a +$900 napi profit (napi leállás – konzisztencia),
* a profitcél + $40 elérése (végleges leállás, a challenge teljesítve).

További korlátok: max. 4 trade/nap, 2 egymást követő vesztes után aznapra leáll, és 15:00 ET után nincs új belépés.

---

## 4. Telepítés

1. Másold a `FlexChallengeBot.cs` és a `FlexBotCore.cs` fájlt ide:
   `Documents\NinjaTrader 8\bin\Custom\Strategies\`
2. NinjaTrader → *New → NinjaScript Editor* → **F5** (compile). Nem szabad hibának lennie.
3. Diagram: **MNQ** (ajánlott) vagy MES, **1 perces** bar, trading hours: **CME US Index Futures ETH**,
   *Days to load*: **legalább 60**.
4. *Strategies* → `FlexChallengeBot` → állítsd be a számlát (FundedNext / Rithmic vagy Tradovate) → *Enabled*.

**Miért MNQ?** A $1 500-as kerethez a mini NQ ($20/pont) túl durva szemcsézettségű: egy 40 pontos stop már $800
lenne. Az MNQ ($2/pont, max. 30 kontraktus) finom méretezést enged.

---

## 5. Validálás élesítés előtt (kötelező)

1. **Strategy Analyzer** backtest, MNQ 1 perc, minimum 2–3 év, *Commission* sablon beállítva,
   `Slippage` = 1 tick. Az Output ablak a végén kiírja a **challenge-szimulációt**: hány egymást követő
   challenge-próbálkozás sikerült vagy bukott el, a medián teljesítési időt napokban, és a meta-modell Brier-
   skilljét. A bot a backtest alatt minden sikeres vagy bukott próbálkozás után új 50K-s challenge-et kezd.
2. **Walk-forward optimalizáció** (Strategy Analyzer → Walk Forward) legfeljebb 3–4 paraméterre, például
   `OrbMaxWidthAtr`, `NoiseBandMultiplier`, `RunnerTargetR`, `MetaProbThreshold`. Az in-sample csúcsra
   optimalizált értékek helyett a stabil plató közepét válaszd.
3. **Market Replay / Playback** legalább 20 nap tick-adaton: ellenőrizd a megbízások életciklusát, a
   részteljesüléseket és a napi leállásokat.
4. **Sim101 élő adaton** legalább 1–2 hétig.
5. Csak ezután jöhet a FundedNext challenge-számla.

> Ez a kód ebben a környezetben C# 6 nyelvi szinten fordult, a platformfüggetlen mag 51 unit teszten ment át.
> Valós piaci adaton futó NinjaTrader-backtest **nem** készült, mert itt nincs sem NinjaTrader, sem piaci adat.
> A jövedelmezőséget a fenti lépésekkel neked kell igazolnod. Garantált teljesítés nem létezik.

---

## 6. Élő üzem

* **Állapotfájl:** `Documents\NinjaTrader 8\FlexChallengeBot\state_<számla>.txt`. Ebben van az egyenleg, a legmagasabb
  EOD egyenleg, a legjobb nap, a kereskedési napok száma és a napi számlálók. Számlánként egy példányt futtass.
* **Szinkron a FundedNext dashboarddal:** ha eltérés van (például kézi trade után vagy gépváltáskor), állítsd be egyszer a
  `Manual current balance`, `Manual highest EOD balance`, `Manual best day profit` és `Manual trading days`
  értékeket, indítsd el a botot, majd állítsd őket vissza 0-ra. Ellenkező esetben minden újraindítás felülírja velük
  az állapotot.
* **Balance source = BrokerAccount:** a napváltáskor és induláskor a bróker `CashValue` értékét veszi át. Ajánlott,
  ha a számlán más is kereskedik.
* **Új challenge:** `Reset live state` = true egy indításra.
* **Funded számla:** `Stop at profit target` = false (nincs cél és konzisztencia), a napi plafont tetszés szerint
  hagyd meg.
* **Hírek:** az `Event blackouts ET` mezőbe írd be a hét FOMC/CPI/NFP időpontjait (`2026-12-09 14:00;2026-12-10 08:30`).
  Ilyenkor a bot nem lép be, és a hír előtt zár.
* **Napló:** `journal_<számla>_<műszer>.csv` – minden trade, a jellemzőkkel és a modell valószínűségével együtt,
  offline elemzéshez.
* **Státuszpanel** a diagram bal felső sarkában: egyenleg, küszöb, hátralévő keret, napi P/L, effektív cél, leállás
  oka, a meta-modell állapota.

---

## 7. Fő paraméterek (alapértékek)

| Csoport | Paraméter | Alap | Megjegyzés |
|---|---|---|---|
| Risk | Risk per trade | 250 | ~1/6 a max loss-nak |
| Risk | Daily loss limit | 450 | 3+ rossz nap fér bele a 1 500-ba |
| Risk | Daily profit cap | 900 | < 1 000 (40% × 2 500), slippage-puffer |
| Risk | Threshold buffer | 150 | a küszöb felett ennyinél napi stop |
| Risk | Max trades / consecutive losses | 4 / 2 | |
| Session | Last entry / Flatten | 15:00 / 15:50 ET | FundedNext: zárás 16:10 ET előtt |
| ORB | OR minutes / entry end | 5 / 11:30 | |
| Noise | Lookback / band / checkpoint | 14 nap / 1,0 / 30 perc | a publikáció értékei |
| Exits | Scalp fraction / target | 0,5 / 1R | |
| Exits | Runner target / trail | 3R / 3 × ATR(1m) | |
| ML | Burn-in / threshold / min skill | 40 / 0,50 / 0,0 | |

---

## 8. Kockázati figyelmeztetés

A határidős kereskedés jelentős veszteséggel járhat. A múltbeli vagy szimulált eredmény nem garantálja a jövőbeli
teljesítményt. A bot a prop-firm szabályainak betartását technikailag a lehető legszigorúbban kezeli, de a
végső felelősség a szabályok aktuális szövegének ellenőrzéséért és a rendszer felügyeletéért a kereskedőé.
