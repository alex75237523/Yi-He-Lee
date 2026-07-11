---
name: daily-ma-precompute
description: Defines the correct scope and boundary for the "每日五日均價策略" (daily moving-average precompute) feature in the YiHeLee WinForms project — it is ONLY a data storage/precompute step (代碼、名稱、收盤價、5日均價、20日均價、60日均價、120日均價 stored in SQLite, anomalies shown in a WinForms tab page), and it must stay completely decoupled from customer-holding comparison (Excel 現價／DDE／StrategyAlert). Use this skill whenever a task touches StockMovingAverage, StockDailyPrice, StockMaster, MA5/MA20/MA60/MA120 calculation, "每日五日均價策略", or any request to add/change a WinForms tab that lists stocks or anomalies — even if the request doesn't mention "precompute" or "customer comparison" explicitly. This skill exists because this exact boundary has been designed incorrectly multiple times before; read it before touching any of the above.
---

# 每日五日均價策略：precompute layer, not customer comparison

## The one rule that keeps getting broken

This feature has two parts that look related but are **not the same system** and must **never be merged, coupled, or made to depend on each other**:

1. **Precompute / storage layer** (this skill's subject): for the official stock universe (TWSE 上市／TPEx 上櫃), store per stock per trade date:

   | 代碼 | 名稱 | 收盤價 | 5日均價 | 20日均價 | 60日均價 | 120日均價 |
   |---|---|---|---|---|---|---|

   That's it. This is a batch job that reads official closing prices and writes rows. It doesn't know or care about any customer, any Excel workbook, or any "現價". Any persisted/exported table using the name `每日五日均價策略` must stay at these seven business columns unless the user explicitly asks for a separate downstream comparison artifact.

2. **Customer comparison** (a *downstream consumer*, already implemented in `StrategyEvaluationService` / `StrategyAlert` / `HoldingStrategyResult`, rendered in `src/YiHeLee.App/Forms/MainForm.Results.cs`): reads a customer's Excel holdings, gets each holding's 現價 from DDE, and compares it against the MA values that the precompute layer *already stored*. This part cares about customers, Excel tabs, and DDE errors.

**Why this distinction matters:** every time these two have gotten tangled — e.g. making the precompute step wait on Excel/DDE state, making its anomalies depend on customer holdings, or trying to render both concerns in the same tab/table — the design has come out wrong and had to be redone. The precompute layer must be able to run, finish, and report its own anomalies with zero knowledge of whether any customer even holds that stock. Comparison against customers happens strictly *after*, by reading the already-stored DB rows — never by reaching back into the precompute logic.

If a task description mixes vocabulary from both ("update the MA calculation so it also checks 現價", "make the anomaly tab show customer alerts", "combine the two result tabs") — stop and flag the conflation to the user rather than implementing it as asked; it's very likely a restatement of the same repeated mistake.

## Where this already lives in the codebase (don't reinvent)

- `StockMaster` — official 代碼／名稱 master, deduped by `StockCode`.
- `StockDailyPrice` — official 收盤價, unique key `StockId + TradeDate`.
- `StockMovingAverage` — 5/20/60/120日均價 cache, unique key `StockId + TradeDate`. Has:
  - `CalculationStatus`: `1`=正常, `2`=交易日數不足, `3`=當日收盤價缺失, `4`=歷史回補失敗
  - `LatestAvailableTradeDate`, `MissingReason` — use these to explain an anomaly, don't invent a new anomaly representation.
- Full schema/rules: `docs/03_資料庫結構.md` ("官方每日收盤價與均線"), `docs/01_需求與規則.md`.

**Anomaly = any row where `CalculationStatus != 1`.** These are precompute-layer anomalies (insufficient history, missing close price, backfill failure) — not DDE/現價 problems, not customer-holding problems.

## Historical backfill is allowed, but exception stocks must be remembered

This has been fixed many times and must not regress:

- The daily MA precompute job must calculate from official close prices that are **already persisted in SQLite**.
- The daily job **may** call `HistoricalBackfillJob` / `IMarketPriceService.BackfillHistoryAsync` when the DB genuinely lacks required official historical close prices for market/date coverage.
- It must **not** keep backfilling forever because one or more stocks are structural exceptions: recent listings, long suspensions, emerging-market stocks whose historical endpoint has no older data, delisted/changed-code cases, or any stock/date combination already confirmed by official sources as unavailable.
- Such exception stocks must be remembered in DB and excluded from future "must keep backfilling" decisions. For those stocks, persist the MA row with `CalculationStatus.InsufficientHistory`, keep unavailable MA values as `null`, set a useful `MissingReason`, and show it in the WinForms anomaly tab.
- Manual/user-triggered historical backfill from the historical prices screen may still exist and should still be able to add data later. If new official data appears and DB becomes sufficient, the remembered exception must not prevent normal MA calculation.

In short: **fill real DB gaps; remember real exceptions; report remaining insufficiency as anomalies instead of walking backward forever.**

## The WinForms anomaly tab

The user wants a WinForms tab page (a `TabPage`, same pattern as the existing tabs) that lists 代碼／名稱／收盤價／5日均價／20日均價／60日均價／120日均價 plus a visible anomaly indicator/reason for any row where `CalculationStatus != 1`.

- Keep it a **separate tab/data source** from the customer-comparison tabs already in `MainForm.Results.cs` (`符合均價條件` / `現價異常` / `無法判斷` — those are built from `StrategyAlert`, driven by Excel/DDE, and must stay untouched by this feature).
- Reuse the existing grid helpers already in `MainForm.Results.cs` (`CreateGrid`, `AddTextColumn`, the `FormatDecimal`/`FormatMa`-style null-safe formatters) instead of inventing a new grid pattern — the codebase already has a consistent look for this.
- Data source is a straight read of `StockMovingAverage` joined to `StockMaster`/`StockDailyPrice` for the target trade date. No Excel, no customer, no DDE involved in producing this tab's rows.

## Excel tab with this name

The Excel worksheet tab literally named `每日五日均價策略` must also follow the precompute/storage column contract unless the user explicitly names a separate customer-comparison export. It may be fed from downstream calculation objects internally, but the persisted worksheet columns are only:

| 代碼 | 名稱 | 收盤價 | 5日均價 | 20日均價 | 60日均價 | 120日均價 |
|---|---|---|---|---|---|---|

Do not add 客戶, 來源頁籤, 原始列號, 市場別, 現價, DDE狀態, DDE錯誤原因, MA比較, 整體結果, 觸發條件, 鉅亨驗證狀態, 計算時間, or other diagnostic/customer-comparison columns to this worksheet. Those belong in the central WinForms customer-comparison result tabs, logs, or a separately named artifact.

Stock codes must be preserved exactly as text when exported or stored. Leading zeroes are part of the stock code: `0050` must remain `0050`, never `50`; `00923`, `00631L`, and `00982A` must likewise keep their original code text. When writing to Excel, set the code column to text format before assigning cell values, because formatting after `Value2` assignment is too late and Excel may already have converted `0050` to `50`.

## Checklist before implementing or changing anything here

1. Does this change require knowing about any specific customer, Excel workbook, or DDE value? If yes, it belongs in `StrategyEvaluationService`/`MainForm.Results.cs`, not here.
2. Are the stored/displayed columns exactly 代碼／名稱／收盤價／5日均價／20日均價／60日均價／120日均價? The WinForms anomaly tab may add anomaly status/reason for visibility, but the persisted `每日五日均價策略` worksheet/table must not add customer-specific columns (現價, 觸發條件, etc.).
3. Are stock codes preserved as text, including leading zeroes (`0050` stays `0050`, not `50`)?
4. Is the anomaly source `StockMovingAverage.CalculationStatus`/`MissingReason`, not `StrategyAlert`?
5. Does the daily job keep backfilling because of exception stocks that cannot reach MA120? If yes, add/use persistent exception memory and let those rows surface as anomalies.
6. If the request is ambiguous about which "每日五日均價策略" (Excel tab vs. WinForms tab) it means, ask before building.
7. After changing behavior here, update `docs/03_資料庫結構.md` / `docs/01_需求與規則.md` / `docs/05_異動紀錄.md` per this repo's own `AGENTS.md` convention — don't leave the docs out of sync with a redesigned boundary.
