@AGENTS.md

# Yi He Lee－Claude Code 專案入口

請先閱讀 `00_START_HERE_給Claude_Code.md`，再進行任何修改。

## 專案定位

- .NET 8 Windows Forms 系統匣應用程式。
- C#、Excel Interop、Playwright／HttpClient、SQLite。
- 每日台北時間 13:35 抓取鉅亨網多頭／空頭排列的集中與店頭完整清單。
- 驗證頁面日期為當日後，保存 SQLite，再比對 Excel 客戶持股。
- MA5、MA20、MA120 任一小於等於進場價／平均價即通知。
- 結果寫入 `每日五日均價策略`，並在螢幕正中央顯示清單。

## 架構邊界

- `YiHeLee.App`：WinForms、NotifyIcon、設定、中央通知。
- `YiHeLee.Application`：排程、日期、重試、流程與策略規則。
- `YiHeLee.Infrastructure`：Crawler、Parser、SQLite Repository、Excel Adapter、Log。
- `YiHeLee.Domain`：資料模型與列舉。

## 最重要更正

- SQLite 是最終決定。
- LocalDB／MSSQL 已取消。
- 資料必須改為可攜式，預設放在 `AppContext.BaseDirectory` 下。
- 現有程式尚未成功編譯，請先 Build 診斷，再小步修復。

> 2026-07-09 更新：已完成首次實際 Build 診斷與修復，Debug／Release Build 與 `dotnet test`（14／14）皆成功；可攜式路徑已改為 `AppContext.BaseDirectory`。詳見 `docs/05_異動紀錄.md`、`docs/06_建置驗證結果.md`。系統匣、中央通知、Excel Interop、鉅亨爬蟲仍待具互動桌面環境的人工驗收，見 `docs/09_人工驗收與完成回報.md`。
