# AGENTS.md

本專案所有回覆、文件與重要註解一律使用繁體中文。Claude Code、Codex 與任何 AI 工具進入本專案時，都必須先讀本檔與下列入口文件，不能只依聊天記憶或單一檔案推測現況。

> Codex 使用者注意：本機已建立個人 skill：`yi-he-lee-project`。之後處理本專案時，優先使用 `$yi-he-lee-project` 進入狀況；Claude Code 則以 `CLAUDE.md` 與 `00_START_HERE_給Claude_Code.md` 為入口。

## 開發前必讀

1. `00_START_HERE_給Claude_Code.md`
2. `PROJECT_INSTRUCTIONS_鉅亨技術指標.md`
3. `README.md`
4. `docs/00_完整需求與目前狀況.md`
5. `docs/01_需求與規則.md`
6. `docs/02_架構與資料流程.md`
7. `docs/03_資料庫結構.md`
8. `docs/04_測試與驗收.md`
9. `docs/07_可攜式SQLite規格.md`
10. `docs/08_Claude_Code修復與實作任務.md`
11. `docs/09_人工驗收與完成回報.md`
12. `docs/10_設定檔settings欄位說明.md`

## 目前專案狀態（2026-07-10）

- GitHub remote 已設定為 `https://github.com/alex75237523/Yi-He-Lee.git`。
- `main` 與 `feature/official-market-price-moving-average` 已推送到 GitHub，最新共同提交為 `1cd5dcc chore: sync VS2022 project to GitHub`。
- VS2022 開啟本資料夾的 `YiHeLee.sln` 時，會透過同一個 Git remote 與 GitHub 串接。
- 最近一次本機驗證：`dotnet test YiHeLee.sln` 通過 129／129；仍有 `SQLitePCLRaw.lib.e_sqlite3 2.1.6` 已知高風險弱點警告與少量 nullable 警告，後續可安排套件升級與修正。

## 最終技術決定

- `.NET 8 Windows Forms`。
- SQLite／`Microsoft.Data.Sqlite`。
- 可攜式資料目錄使用 `AppContext.BaseDirectory`。
- 禁止改用 MSSQL、LocalDB、MDF、LDF。
- 固定台北時間 13:35，日期一律使用 Asia/Taipei 當日。
- 頁面日期不等於當日不得寫入，不得回抓前一交易日。
- 固定兩個鉅亨來源均需擷取集中市場、店頭市場完整清單。
- WinForms 只負責畫面；流程在 Application Service；資料存取在 Repository；網站解析在 Provider／Parser；Excel 操作集中在 Excel Service。
- 資料庫使用關聯式設計、唯一鍵、參數化 SQL、交易與冪等機制。
- 不提交 `bin`、`obj`、`.vs`、`logs`、DLL、EXE、SQLite 執行資料、設定、密碼、API Key、Token。
- 不直接 Push 到 `main`；除非使用者明確要求「把目前專案推上 GitHub／同步 main」。
- 修改後實際執行 Restore、Build、Test、Publish；無法執行必須明確回報。

## AI 接手工作方式

- 先看 `git status --short --branch`，確認目前分支與使用者未提交變更。
- 先讀需求、架構、資料庫、驗收與異動紀錄，再修改程式。
- 小步修正，保留現有架構；不要把 WinForms、SQLite、Excel Interop、TWSE／TPEx Provider 任意換掉。
- 程式行為、設定欄位、資料表、UI 流程或驗收規則有變動時，同步更新相關 docs 與 `docs/05_異動紀錄.md`。
- 回報時列出實際做了什麼、Build/Test 結果、尚未實機驗證項目與風險。

## 重要歷史更正

> 2026-07-09 更新：已修正為 `AppContext.BaseDirectory`，並同步更新 `README.md`；詳見 `docs/05_異動紀錄.md`。同日也已完成首次實際 `dotnet restore／build／test` 並全數成功，詳見 `docs/06_建置驗證結果.md`。
>
> 2026-07-09 之後：正式均價來源已改為 TWSE／TPEx 官方每日收盤價，由系統自行計算 MA5／MA20／MA60／MA120；鉅亨網多頭／空頭清單保留為清單保存與交叉驗證，不再作為正式均價來源。鉅亨網擷取為 best-effort，不阻擋官方價格與策略流程。
>
> 2026-07-11 修正：歷史回補完成判斷改為逐檔股票檢查（不再只看市場整體交易日數）；新增 `StockCodeNormalizer`／`StockIdentityResolver`／`StockIdentityResolutionService` 統一股票代碼正規化、格式辨識與官方主檔補零驗證；Excel 表頭新增「現貨現價」同義字，並新增其他表格邊界偵測；Excel 輸出擴充為 14 欄含診斷資訊。分支 `fix/per-stock-moving-average-history`；`dotnet test` 通過 211／211（另 4 個 Integration 測試）。詳見 `docs/05_異動紀錄.md`。
>
> **2026-07-13 最新正式更正比較方向**：2026-07-12 曾記載「進場價/平均價」與「現價」必須同時大於或等於均價才成立，2026-07-13 稍早曾記載 `均價 >= 價格`；這兩個方向都已被最新需求取代。現行規則為 MA5／MA20／MA120 任一均價只要 `均價 <= 進場價/平均價` 或 `均價 <= 現價` 其中一項成立即通知；不得再用 `均價 >= 價格` 判斷通知。MA60 仍只保存與顯示、不參與觸發。「進場價/平均價」與「現價」仍是兩個獨立欄位，任一欄位無效時不得判斷並須保留對應異常通知。詳見 `docs/01_需求與規則.md`、`docs/05_異動紀錄.md`。
