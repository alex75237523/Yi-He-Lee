@AGENTS.md

# Yi He Lee－Claude Code 專案入口

請先閱讀 `AGENTS.md` 與 `00_START_HERE_給Claude_Code.md`，再進行任何修改。本專案也有 Codex 專用 skill：`yi-he-lee-project`；若工作在 Codex 端進行，請以該 skill 進入狀況。兩邊都必須以同一份 repo 文件為準，不以單次聊天記憶取代文件。

**每日五日均價策略（precompute／WinForms 異常頁籤）相關任務，一律先讀 `.claude/skills/daily-ma-precompute/SKILL.md`**：這是一個純資料儲存／均價換算前置作業（代碼、名稱、收盤價、5日均價、20日均價、60日均價、120日均價），與客戶持股比對（Excel 現價／DDE／`StrategyAlert`）是兩個完全不相關、不得耦合的系統，過去多次被誤設計成同一件事。

## 專案定位

- .NET 8 Windows Forms 系統匣應用程式。
- C#、Excel Interop、Playwright／HttpClient、SQLite。
- 每日台北時間 13:35 抓取鉅亨網多頭／空頭排列的集中與店頭完整清單。
- 鉅亨網清單驗證頁面日期為當日後保存 SQLite，但目前只作清單保存與交叉驗證。
- 正式均價來源為 TWSE／TPEx 官方每日收盤價，由系統依有效交易日自行計算 MA5／MA20／MA60／MA120。
- 每一條均價（MA5／MA20／MA120）只要 `均價 <= 進場價/平均價` 或 `均價 <= 現價` 其中一項成立就通知（2026-07-13 最新正式更正比較方向，取代稍早 `均價 >= 價格` 的錯誤方向）；「現價」串接外部 DDE，「進場價/平均價」非 DDE 欄位；任一欄位無法判讀時列入對應的異常清單告知，不得判斷、不得混用、不得互相代替。
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
- 現有程式已可 Build/Test；修改前仍須先讀文件與程式碼，再小步修復。
- 不直接 Push 到 `main`；除非使用者明確要求同步 GitHub/main。

> 2026-07-09 更新：已完成首次實際 Build 診斷與修復，Debug／Release Build 與 `dotnet test`（14／14）皆成功；可攜式路徑已改為 `AppContext.BaseDirectory`。詳見 `docs/05_異動紀錄.md`、`docs/06_建置驗證結果.md`。系統匣、中央通知、Excel Interop、鉅亨爬蟲仍待具互動桌面環境的人工驗收，見 `docs/09_人工驗收與完成回報.md`。
>
> 2026-07-10 更新：本地 Git remote 已串到 `https://github.com/alex75237523/Yi-He-Lee.git`，`main` 與 `feature/official-market-price-moving-average` 已推送；最新驗證 `dotnet test YiHeLee.sln` 通過 129／129，但有 `SQLitePCLRaw.lib.e_sqlite3 2.1.6` 高風險弱點警告需後續處理。
>
> **2026-07-13 最新正式更正比較方向**：2026-07-12 的雙價格 AND 規則與 2026-07-13 稍早的 `均價 >= 價格` 方向皆已被取代。現行規則為 MA5／MA20／MA120 任一均價只要小於或等於「進場價/平均價」或「現價」其中一項即成立；MA60 不參與觸發。詳見 `docs/01_需求與規則.md`、`docs/05_異動紀錄.md`。
