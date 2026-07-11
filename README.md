# Yi He Lee

> Claude Code 接手時，第一個檔案請讀：`00_START_HERE_給Claude_Code.md`。

`Yi He Lee` 是 .NET 8 Windows Forms 常駐系統匣工具，用於每日台北時間 13:35 擷取鉅亨網台股技術指標、保存至可攜式 SQLite、讀取已開啟的客戶 Excel 持股，並在任一均價條件成立時於螢幕正中央顯示通知清單。

## 最終資料庫決定

- 使用 SQLite／`Microsoft.Data.Sqlite`。
- 不使用 MSSQL、SQL Server Express LocalDB、MDF、LDF。
- 正式可攜式路徑應為：

```text
<程式目錄>\settings.json
<程式目錄>\Data\yi-he-lee.db
<程式目錄>\Logs\
<程式目錄>\Backups\
```

`AppPaths.cs` 已改為 `AppContext.BaseDirectory`，符合 `docs/07_可攜式SQLite規格.md`。


`Yi He Lee` 是 .NET 8 Windows Forms 常駐系統匣工具，用於每日台北時間 13:35 擷取鉅亨網台股技術指標、保存至 SQLite、讀取已開啟的客戶 Excel 持股，並在任一均價條件成立時於螢幕正中央顯示通知清單。

## 目前完成範圍

- 固定來源（多頭／空頭排列完整清單保存與交叉驗證，非正式均價來源）：
  - 股價多頭排列：`https://www.cnyes.com/twstock/a_technical4.aspx`
  - 股價空頭排列：`https://www.cnyes.com/twstock/a_technical5.aspx`
- 每個固定來源都擷取集中市場及店頭市場。
- **正式均價來源（2026-07-09 起）**：上市股票改用臺灣證券交易所（TWSE）官方每日收盤價，上櫃股票改用證券櫃檯買賣中心（TPEx）官方每日收盤價；MA5／MA20／MA60／MA120 由本系統依有效交易日自行計算，不再採用鉅亨網頁面上的均價數字。詳見 `docs/03_資料庫結構.md`與`PROJECT_INSTRUCTIONS_鉅亨技術指標.md` 第九節。
- 強制使用 `Asia/Taipei` 當日日期；頁面／來源日期不是當日就拒絕寫入，不回抓前一交易日。
- SQLite 關聯式資料庫、唯一鍵、交易及重跑冪等機制。
- 掃描所有客戶頁籤，不要求一定出現「自持股」文字。
- 排除 `總表`、`每日五日均價策略`。
- 排除包含「出場價」的已出場表格。
- 預設排除股名儲存格填滿色 `#92D050` 的持股列，也可加入文字標記。
- 任一條件成立即通知（2026-07-11 起比較基準改為 Excel「現價」欄位，該欄位串接外部 DDE）：
  - `5 日均價 <= 現價`
  - `20 日均價 <= 現價`
  - `120 日均價 <= 現價`
- 「現價」為外部 DDE 連結值：若儲存格為 #N/A 等錯誤值、空白、0 或無法解析的文字，該持股不得判斷、不得以其他價格代替，一律列入「現價異常」清單於畫面中央告知使用者。
- 結果寫入 `每日五日均價策略`（14 欄，含市場別、有效交易日數、最新收盤日期、計算狀態、缺少原因等診斷欄位），並於螢幕正中央顯示客戶、股票及觸發均價清單。
- **逐檔歷史完整性驗證（2026-07-11 起）**：MA120 所需歷史資料的回補完成判斷改為逐檔股票檢查，不再只看市場整體交易日數是否足夠；股票代碼統一由 `StockCodeNormalizer`／`StockIdentityResolver`／`StockIdentityResolutionService` 正規化、格式辨識與官方主檔補零驗證，7～8 位數金額不會被誤判為股票代碼，權證明確排除於均線策略之外。
- Excel 忙碌、對話框、正在編輯儲存格等暫時錯誤會短暫重試；只有可重試失敗才會由排程再次執行。唯讀、工作表保護、設定錯誤等問題會立即停止並要求人工修正。
- 程式常駐 Windows 右下角系統匣，可立即執行、開啟設定、開啟 Excel、查看 Log、選擇以系統管理員重新啟動。
- 可設定 N 個網址；不同網站應新增獨立 `ISourceCrawler`，不可把不同網頁解析規則混在同一 Provider。非必要來源若任一市場失敗，該來源整批不寫入，不影響兩個固定必要來源的成功判定。
- **歷史收盤價查詢與手動回補（2026-07-09 新增）**：系統匣選單「歷史收盤價」可查詢分頁歷史收盤價（含 MA5／MA20／MA60／MA120，資料不足顯示文字而非0），並可手動「立即回補」最近 N 個有效交易日（預設5日，可調整），以市場＋交易日期為工作單位有限並行抓取（預設4、上限8），即時顯示整體與工作明細進度，可取消，重新整理仍可回復進度；另提供與鉅亨網多頭／空頭清單交叉驗證。詳見 `docs/02_架構與資料流程.md`、`docs/03_資料庫結構.md`。

## 技術架構

```text
YiHeLee.App             WinForms、NotifyIcon、中央結果視窗、設定畫面、歷史收盤價查詢／回補進度畫面
YiHeLee.Application     排程流程、驗證、均線策略、官方價格協調（MarketPriceService）、歷史回補並行協調（StockHistoryImportService）、鉅亨交叉驗證、介面
YiHeLee.Infrastructure  Playwright 爬蟲、TWSE／TPEx HTTP Provider、Excel Interop、SQLite、Log、設定檔
YiHeLee.Domain          Entity、Enum、設定模型
tests/YiHeLee.Tests     策略、排除規則、設定、鉅亨表格 Parser、TWSE／TPEx Parser、均線計算、官方價格服務、歷史回補並行／進度、歷史收盤價查詢、鉅亨交叉驗證、SQLite 測試
```

## 執行需求

- Windows 10／11 x64。
- .NET 8 SDK（開發）或 .NET 8 Desktop Runtime（Framework-dependent 發佈）。
- Microsoft Excel 桌面版。
- Microsoft Edge；若 Edge 無法供 Playwright 啟動，可執行安裝 Chromium 腳本。
- 程式與 Excel 必須使用相同權限層級。一般方式開啟 Excel 時，程式也建議以一般使用者執行。

## 建置

```powershell
cd <專案目錄>
.\scripts\build.ps1
```

或：

```powershell
dotnet restore .\YiHeLee.sln
dotnet build .\YiHeLee.sln -c Release
dotnet test .\tests\YiHeLee.Tests\YiHeLee.Tests.csproj -c Release --no-build
```

## 安裝 Playwright Chromium（Edge 無法啟動時才需要）

```powershell
.\scripts\install-playwright-browser.ps1
```

## 發佈

```powershell
.\scripts\publish-win-x64.ps1
```

輸出位置：`publish\win-x64`。

## 首次操作

1. 啟動 `Yi He Lee.exe`。
2. 在設定畫面選擇實際 Excel 檔案。
3. 確認輸出頁籤為 `每日五日均價策略`。
4. 保留固定鉅亨來源；未來新網址可新增，但必須有對應 Provider 才會執行。
5. 先以 Excel 桌面版開啟指定活頁簿。
6. 右下角系統匣按右鍵，可選「立即執行」測試。

## Excel 使用者注意事項

執行前請按 Enter 或 Esc 結束儲存格編輯，並關閉另存新檔、列印、尋找取代等 Excel 對話框。執行期間不要關閉 Excel、重新命名或刪除輸出頁籤、執行巨集或另存新檔。程式寫入前會以 Excel `SaveCopyAs` 建立包含目前記憶體狀態的備份；完成時會儲存整份活頁簿，因此也會一併儲存使用者尚未儲存的變更。

### 找不到已開啟活頁簿時

程式會依序使用 Running Object Table、目前作用中的 Excel，以及 Windows 上所有 Excel 視窗尋找指定活頁簿，並把探測到的活頁簿路徑與錯誤寫入 Log。若仍失敗，請依中央視窗訊息處理：

- Excel 上方出現黃色「受保護的檢視」列時，先按「啟用編輯」；受保護檢視不可寫入，程式會直接要求人工處理，不再無效重試。
- 若 Excel 已開啟同名檔案，但路徑與設定不同，請在設定重新選取目前實際開啟的檔案；程式不會只憑檔名連接，避免寫錯副本。
- 若 Log 顯示已偵測到 Excel 視窗但無法取得 NativeOM，自動化介面通常被權限隔離；請讓 Excel 與 Yi He Lee 都以一般權限開啟，或兩者都以系統管理員權限開啟。
- 活頁簿剛開啟、尚未完成 COM 註冊時，程式會依 `ExcelShortRetryCount`／`ExcelShortRetryDelaySeconds` 先做短暫重試。

## 資料位置

資料一律以程式所在目錄（`AppContext.BaseDirectory`）為根目錄，整個資料夾可直接複製搬移到其他可寫路徑：

```text
<程式目錄>\
├─ Yi He Lee.exe
├─ settings.json
├─ Data\yi-he-lee.db
├─ Logs\
└─ Backups\
```

不建議放在 `C:\Program Files` 等需要系統管理員權限的目錄；建議放在桌面、文件、D 槽或其他可寫資料夾。

## 權限設計

Manifest 預設使用 `asInvoker`，確保能連接一般權限啟動的 Excel。系統匣選單提供「以系統管理員重新啟動」。提升後，Excel 也必須用系統管理員權限開啟，否則 Windows UIPI／COM 權限隔離可能讓程式找不到活頁簿。

## 重要限制

- 目前只有 `CnyesTechnicalAlignment` Provider；新增不同網頁時，需要新增專用 Crawler／Parser。
- 若頁面出現分頁，現行 Provider 會拒絕只寫第一頁，避免保存不完整資料；必須先擴充完整分頁遍歷。
- 本程式只判斷兩個排列清單中實際出現的股票；未出現在兩份完整清單中的持股會列在「無法判斷」分頁，不會猜測或使用昨日資料。
