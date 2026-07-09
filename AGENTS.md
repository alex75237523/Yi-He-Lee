# AGENTS.md

本專案所有回覆、文件與重要註解一律使用繁體中文。

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
- 不直接 Push 到 `main`。
- 修改後實際執行 Restore、Build、Test、Publish；無法執行必須明確回報。

## 目前已知待修正

`src/YiHeLee.App/AppPaths.cs` 與既有 README 使用 `%LOCALAPPDATA%`，與最新可攜式需求衝突。請小步修正並同步文件與測試，不要重寫整個專案。

> 2026-07-09 更新：已修正為 `AppContext.BaseDirectory`，並同步更新 `README.md`；詳見 `docs/05_異動紀錄.md`。同日也已完成首次實際 `dotnet restore／build／test` 並全數成功，詳見 `docs/06_建置驗證結果.md`。
