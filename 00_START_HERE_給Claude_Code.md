# Yi He Lee－Claude Code 完整接手指令

> 本文件是 Claude Code 進入專案時的第一入口。請不要只讀單一程式檔，也不要直接重寫專案。

## 一、目前狀況

- 專案名稱：`Yi He Lee`。
- 使用者已將既有原始碼放在本機端。
- 目前使用者尚未成功完成編譯，因此第一個任務是「檢查現況、實際 Build、逐項修正」，不是重新產生一套新專案。
- 技術棧固定為：`.NET 8`、Windows Forms、系統匣、Excel Interop、Playwright／HttpClient、SQLite。
- **資料庫最終決定為 SQLite。先前討論過的 MSSQL／SQL Server Express LocalDB 已取消，禁止改成 LocalDB、MDF、LDF 或 Microsoft.Data.SqlClient。**
- 使用者重視可攜式，資料與設定應跟著程式資料夾搬移。

## 二、開始前必讀順序

請依序完整閱讀：

1. `AGENTS.md`
2. `CLAUDE.md`
3. `PROJECT_INSTRUCTIONS_鉅亨技術指標.md`
4. `README.md`
5. `docs/00_完整需求與目前狀況.md`
6. `docs/01_需求與規則.md`
7. `docs/02_架構與資料流程.md`
8. `docs/03_資料庫結構.md`
9. `docs/04_測試與驗收.md`
10. `docs/07_可攜式SQLite規格.md`
11. `docs/08_Claude_Code修復與實作任務.md`
12. `docs/09_人工驗收與完成回報.md`

完成閱讀後，再檢查 Solution、所有 `.csproj`、目前程式碼、資料庫 Schema、設定檔與測試。

## 三、不可改變的核心需求

### 1. 執行型態

- `.NET 8 Windows Forms`。
- 程式常駐 Windows 右下角系統匣。
- 沒有主要視窗時仍要持續執行。
- 系統匣選單至少提供：立即執行、開啟設定、開啟 Excel、查看 Log、重新啟動、結束。
- 通知結果不是只顯示右下角氣泡；正式結果與錯誤必須顯示在螢幕正中央。
- 同一次執行只顯示一個彙整清單，不要每一檔股票跳一個視窗。

### 2. 排程

- 時區固定 `Asia/Taipei`。
- 每日台北時間 `13:35` 執行。
- 手動執行與排程執行共用同一個執行鎖，禁止同時重複執行。
- 程式若在 13:35 後才啟動，應檢查當日是否尚未成功；尚未成功時可立即執行。
- 所有請求日期、頁面日期、SQLite 交易日期、通知日期，必須是台北執行當日。
- 網站尚未更新、休市、例假日、網路失敗或解析失敗時，不得自動改抓前一交易日，也不得把昨天資料寫成今天。

### 3. 固定資料來源

必須保留以下兩個來源，不可刪除或停用：

- 多頭排列：`https://www.cnyes.com/twstock/a_technical4.aspx`
- 空頭排列：`https://www.cnyes.com/twstock/a_technical5.aspx`

每一個來源都必須抓取：

- 集中市場完整清單。
- 店頭市場完整清單。

不得只抓預設市場、第一頁、前 50 筆或部分資料。若網站有分頁、載入更多、虛擬清單或 API 分批回傳，必須完整遍歷後才算成功。

### 4. 可擴充來源

- 設定畫面允許輸入 N 個網址。
- 每個網站的 HTML、API、切換市場與日期方式可能不同。
- 不同網站必須新增獨立 `ISourceCrawler`／Provider／Parser。
- 核心排程、SQLite、Excel 與策略判斷不能綁死在單一網站。
- 自訂網址若沒有對應 Provider，設定畫面要清楚提示，不能假裝成功。

### 5. SQLite 可攜式資料庫

- 使用 `Microsoft.Data.Sqlite`。
- 禁止改用 MSSQL、LocalDB、SQL Server Express、MDF、LDF。
- 預設資料根目錄使用 `AppContext.BaseDirectory`，讓整個資料夾可複製搬移。
- 建議結構：

```text
Yi He Lee.exe
settings.json
Data\yi-he-lee.db
Logs\
Backups\
```

- 目前既有 `AppPaths.cs` 使用 `%LOCALAPPDATA%\Yi He Lee`，這與最新可攜式需求衝突，必須列為待修正項目。
- 可攜式模式請提醒使用者不要把程式放在沒有寫入權限的 `C:\Program Files`；建議放在桌面、文件、D 槽或其他可寫資料夾。
- 使用關聯式資料表、唯一鍵、參數化 SQL、Transaction、Upsert、外鍵及冪等機制。
- SQLite 若使用 WAL 模式，備份不可只在資料庫開啟時單純 `File.Copy` 主 `.db`；應使用 SQLite Backup API、`VACUUM INTO` 或先安全 checkpoint 後備份。

### 6. Excel 檔案

- 使用者必須能在設定畫面選擇 Excel 完整路徑。
- Excel 可能已開啟，而且使用者同時操作其他頁籤。
- 程式應連接已開啟的活頁簿，不要另外以檔案函式庫再開同一檔案寫入。
- 程式與 Excel 必須處於相同 Windows 權限層級。
- 預設以一般權限執行；系統匣可以提供「以系統管理員重新啟動」，但提升後 Excel 也必須用管理員權限開啟。

### 7. Excel 頁籤掃描

排除：

- `總表`
- `每日五日均價策略`

其餘客戶頁籤都要掃描。注意：

- 有些客戶頁籤有「自持股」文字。
- 有些客戶頁籤沒有「自持股」文字，但仍有有效持股。
- 因此不可只靠「自持股」標題。
- 應以表頭結構辨識，例如：代號／代碼、股名／名稱、現價；張數可作輔助欄位。（2026-07-11 更正：原「進場價／平均價」為口誤，正確欄位為「現價」，該欄位串接外部 DDE。）
- 股票代碼必須當字串讀取，優先使用 Excel 顯示文字，保留 `0050`、`00923`、`00631L`、`00982A` 等前導零及英文字尾。

### 8. 不需要判斷的 LIST／持股列

使用者已補充：某些具有特定顏色與文字結構的 LIST 不要抓來判斷。

正式規則：

- 文字／表頭結構為主要判斷。
- 顏色為輔助判斷。
- 表頭出現 `出場價` 或 `出場日`，視為已出場 LIST，整段不納入。
- 列內出現 `不判斷`、`不用判斷`、`忽略`、`已出場`、`暫停判斷` 等設定文字時，該列略過。
- 目前既有設定包含排除填滿色 `#92D050`；Claude Code 必須以實際工作簿再次確認，不可因猜色而誤排除正常持股。
- 不可只看一個顏色就排除所有相同色儲存格；至少要搭配區塊表頭、文字或可設定規則。
- 顏色值應正規化為 RGB／ARGB 後比對，並處理 Theme Color、Tint 或無填滿色。

### 9. 均價策略

每一筆有效持股，以股票代碼比對當日技術資料。

只要以下任一條件成立，就產生通知：

```text
5 日均價 <= 現價
或
20 日均價 <= 現價
或
120 日均價 <= 現價
```

> 2026-07-11 更正：比較基準由「進場價／平均價」改為 Excel「現價」欄位。「現價」串接外部 DDE（看盤軟體），
> 儲存格為 #N/A 等錯誤值、空白、0 或無法解析的文字時，該持股不得判斷、不得以其他價格代替，
> 必須列入「現價異常」清單明確告知使用者。

- 不是三項都要成立。
- 同一筆持股即使同時符合多項，也只顯示一列，並列出所有實際觸發項目。
- 60 日均價要保存及顯示，但目前不參與通知條件。
- 同一股票若同日出現在多頭與空頭來源，不能產生重複通知。若技術數值一致可合併；若數值不一致，要記錄資料衝突，不可任意挑一筆。
- 未出現在兩份完整清單中的持股，列為「無法判斷／缺少當日技術資料」，禁止使用昨日資料補值。

### 10. 通知內容

螢幕中央的結果清單至少顯示：

- 客戶名稱。
- 客戶頁籤。
- 股票代碼。
- 股票名稱。
- 現價（Excel「現價」欄位，DDE；無法判讀時顯示文字說明）。
- 張數（若有）。
- 當日收盤價。
- 5 日均價。
- 20 日均價。
- 60 日均價。
- 120 日均價。
- 實際觸發條件。
- 交易日期。
- 資料來源／市場別（可在詳細資訊顯示）。

成功畫面至少分成：

1. 符合均價條件。
2. 無法判斷。

失敗畫面要明確顯示失敗階段、原因、已重試次數、下一次是否會重試。

### 11. Excel 寫入

- 結果寫到 `每日五日均價策略`。
- 同一天重跑採覆寫／重建，不可一直往下追加造成重複。
- 寫入前備份當前活頁簿。
- 寫入後重新讀取關鍵欄位或筆數驗證。
- 驗證成功後才呼叫 `Workbook.Save()`。
- 不得關閉使用者的 Excel，不得呼叫 `Application.Quit()`。
- 程式完成後要釋放自身 COM 參考。

### 12. Excel 防呆與重試

執行前中央提示使用者：

- 先按 Enter 或 Esc 結束儲存格編輯。
- 關閉另存新檔、列印、尋找取代等對話框。
- 更新期間不要關閉 Excel。
- 不要重新命名或刪除輸出頁籤。
- 不要執行大型巨集、Power Query、樞紐分析更新或另存新檔。

可短暫重試：

- Excel 正忙碌。
- 使用者正在編輯儲存格。
- COM 呼叫被拒絕。
- 活頁簿剛開啟尚未能連接。
- 網路暫時失敗。
- 網站尚未更新當日資料。

不可無效重試：

- 活頁簿唯讀。
- 受保護檢視。
- 輸出頁籤受保護且程式沒有合法解除方式。
- Excel 檔案路徑錯誤。
- 頁籤名稱設定錯誤。
- Provider 不存在。
- 備份資料夾無權限或磁碟空間不足。

所有重試與最終結果都要寫入 Log 與 Job 紀錄。禁止無限重試。

## 四、資料庫最低需求

至少保留：

- `Stocks`
- `TechnicalIndicatorDaily`
- `JobRuns`
- `JobRunDetails`
- `CustomerHoldingSnapshots`
- `StrategyAlerts`

技術資料唯一鍵：

```text
TradeDate + IndicatorType + MarketType + StockCode
```

每筆技術資料至少保存：

- 交易日期。
- 資料類型。
- 市場別。
- 股票代碼、名稱。
- 收盤價。
- 5、20、60、120 日均價。
- 來源網址。
- 擷取開始及完成時間。
- 建立及更新時間。

## 五、架構規則

- `YiHeLee.App`：WinForms、NotifyIcon、中央通知、設定畫面。
- `YiHeLee.Application`：排程、日期規則、重試分類、流程、策略判斷。
- `YiHeLee.Infrastructure`：Crawler、Parser、Excel Adapter、SQLite Repository、Log、設定。
- `YiHeLee.Domain`：Entity、Enum、設定模型。
- Form 事件不可包含爬蟲、SQL、策略或 Excel 商業流程。
- Repository 只做 CRUD／Query／資料轉換。
- SQL 全部參數化。
- 重要註解、文件與回報使用繁體中文。

## 六、Claude Code 第一階段執行方式

### 步驟 1：建立安全分支

- 先執行 `git status`。
- 不得直接 Push 到 `main`。
- 若目前沒有 Git，先不要自行上傳遠端；只在本機修正。

### 步驟 2：建立現況報告

執行並記錄：

```powershell
dotnet --info
dotnet restore .\YiHeLee.sln
dotnet build .\YiHeLee.sln -c Debug
dotnet build .\YiHeLee.sln -c Release
dotnet test .\tests\YiHeLee.Tests\YiHeLee.Tests.csproj -c Release
```

- 不可只說「應該可以」；要貼出實際錯誤摘要。
- 先修第一個根因，再重新 Build，不要同時大改所有檔案。
- 不要因為套件錯誤就任意更換技術棧。

### 步驟 3：優先修正已知衝突

1. SQLite 必須保留。
2. 將 `%LOCALAPPDATA%` 路徑改成可攜式程式目錄。
3. 同步更新 README、設定說明、備份與 Log 路徑。
4. 確認 `Microsoft.Data.Sqlite` 正常還原。
5. 確認 WinForms、Excel Interop、Playwright 在 `net8.0-windows` 可建置。
6. 確認 x64／AnyCPU 與 Office 位元數風險有清楚說明。
7. 確認 manifest 預設 `asInvoker`，管理員模式只作選項。
8. 確認 `settings.json` 第一次啟動能在程式資料夾建立。

### 步驟 4：完成自動與人工驗證

- 自動測試必須通過。
- 執行 SQLite Schema 建立及重跑測試。
- 在 Windows 實機測試 Excel 已開啟、忙碌、唯讀、保護、頁籤不存在等狀況。
- 實際測試系統匣及中央通知。
- 實際測試鉅亨兩頁、兩市場、當日日期及完整筆數。

## 七、禁止事項

- 禁止改用 MSSQL／LocalDB。
- 禁止自動抓昨日資料補今天。
- 禁止只抓第一頁或前 50 筆。
- 禁止把商業邏輯塞進 Form。
- 禁止直接關閉使用者 Excel。
- 禁止直接 Push 到 `main`。
- 禁止提交 `bin`、`obj`、`.vs`、`logs`、DLL、EXE、實際 SQLite DB、密碼、Token。
- 禁止在未驗證現有程式前重寫整個 Solution。

## 八、完成回報格式

```text
完成項目：
修改檔案：
資料庫影響：
排程與日期驗證：
擷取結果與筆數：
Excel 防呆與重試：
通知與系統匣：
架構檢查：
文件更新：
Build／測試結果：
尚未驗證項目：
風險提醒：
下一步建議：
```
