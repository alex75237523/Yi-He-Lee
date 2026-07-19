# Yi He Lee

> Claude Code 接手時，第一個檔案請讀：`00_START_HERE_給Claude_Code.md`。

`Yi He Lee` 是 .NET 8 Windows Forms 常駐系統匣工具，用於每日台北時間 13:35 擷取鉅亨網台股技術指標、保存至可攜式 SQLite、讀取已開啟的客戶 Excel 持股，並在符合複合策略條件（進場價/平均價 > MA20 且 現價 < MA5）時於螢幕正中央顯示通知清單。

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


`Yi He Lee` 是 .NET 8 Windows Forms 常駐系統匣工具，用於每日台北時間 13:35 擷取鉅亨網台股技術指標、保存至 SQLite、讀取已開啟的客戶 Excel 持股，並在符合複合策略條件（進場價/平均價 > MA20 且 現價 < MA5）時於螢幕正中央顯示通知清單。

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
- **歷史規則（皆已作廢）**：2026-07-11「單一現價比較」、2026-07-12「雙價格 AND」、2026-07-13「MA5／MA20／MA120 任一均價 >= 進場價/平均價或現價（OR）」規則，均已由 2026-07-19 新複合條件取代，不得再套用。
- **2026-07-19 現行唯一正式規則**：客戶 Excel 持股表同時有「進場價/平均價」與「現價」兩個完全獨立的欄位，禁止混用、禁止互相代替。唯一通知條件是下列複合條件，**必須兩項同時成立**：

  ```text
  進場價/平均價 > MA20  AND  現價 < MA5
  ```

  - 邊界一律嚴格大於／小於：`進場價/平均價 == MA20` 不觸發、`現價 == MA5` 不觸發。
  - 只有其中一項成立不得觸發；MA60／MA120 只保存與顯示，不參與觸發（不再有任一均線成立即通知的 OR 邏輯）。
  - 判斷前 `進場價/平均價`、`現價`、`MA5`、`MA20` 四者都必須有效；`MA5` 或 `MA20` 缺少即無法判斷、明確顯示缺少哪一條，不得用其他均線替代。
  - 資料模型欄位沿用但語意更新：`TriggeredMa5`＝「現價 < MA5」、`TriggeredMa20`＝「進場價/平均價 > MA20」、`TriggeredMa120` 固定 false；整體觸發＝`TriggeredMa5 && TriggeredMa20`。
- 「現價」為外部 DDE 連結值；「進場價/平均價」不是 DDE 欄位，是一般手動或公式輸入的成本價。兩者各自可能為 #N/A 等 Excel 錯誤值、空白、0、負數或無法解析的文字；任一價格無效時該持股不得判斷、不得以其他價格（含收盤價、昨日現價、對方欄位）代替，且必須分別列入「進場價／平均價異常」或「現價異常」清單於畫面中央告知使用者，兩者同時無效時兩個原因都必須讓使用者看見。**DDE 現價異常與進場價/平均價異常都只影響最後的複合條件比較，不得影響持股辨識、官方收盤價擷取、SQLite 寫入、歷史回補、均線計算、鉅亨交叉驗證及 Excel 均線輸出**（2026-07-11 強化並補齊自動測試，見 `docs/01_需求與規則.md`）。
- `每日五日均價策略` 只保存 7 欄均價前置資料：代碼、名稱、收盤價、5日均價、20日均價、60日均價、120日均價。客戶、Excel 現價、DDE 狀態、觸發條件與診斷資訊只在螢幕中央結果頁籤呈現，不寫入此頁籤。
- **逐檔歷史完整性驗證（2026-07-11 起）**：MA120 所需歷史資料的回補完成判斷改為逐檔股票檢查，不再只看市場整體交易日數是否足夠；股票代碼統一由 `StockCodeNormalizer`／`StockIdentityResolver`／`StockIdentityResolutionService` 正規化、格式辨識與官方主檔補零驗證，7～8 位數金額不會被誤判為股票代碼，權證明確排除於均線策略之外。
- Excel 忙碌、對話框、正在編輯儲存格等暫時錯誤會短暫重試；只有可重試失敗才會由排程再次執行。唯讀、工作表保護、設定錯誤等問題會立即停止並要求人工修正。
- 程式常駐 Windows 右下角系統匣，可立即執行、開啟設定、開啟 Excel、查看 Log、選擇以系統管理員重新啟動。
- 可設定 N 個網址；不同網站應新增獨立 `ISourceCrawler`，不可把不同網頁解析規則混在同一 Provider。非必要來源若任一市場失敗，該來源整批不寫入，不影響兩個固定必要來源的成功判定。
- **歷史收盤價查詢與手動回補（2026-07-09 新增）**：系統匣選單「歷史收盤價」可查詢分頁歷史收盤價（含 MA5／MA20／MA60／MA120，資料不足顯示文字而非0），並可手動「立即回補」最近 N 個有效交易日（預設5日，可調整），以市場＋交易日期為工作單位有限並行抓取（預設4、上限8），即時顯示整體與工作明細進度，可取消，重新整理仍可回復進度；另提供與鉅亨網多頭／空頭清單交叉驗證。詳見 `docs/02_架構與資料流程.md`、`docs/03_資料庫結構.md`。
- **盤中基準自動準備與快速路徑（2026-07-13 新增）**：盤中判斷會先解析 `evaluationDate` 的上一交易日基準；若該基準日官方收盤價或 MA5／MA20／MA60／MA120 缺漏，會在同一次呼叫內自動補齊、重算、重新驗證基準，成功後立即讀取 Excel 最新價格並判斷，不要求使用者再按第二次。基準完成後的後續手動執行、下一個 30 秒 Tick、程式重啟後下一輪，都會直接從 SQLite 推導 Ready 並沿用已保存均價，只重新讀取客戶價格，不重複回補或重算同日均價。

## 技術架構

```text
YiHeLee.App             WinForms、NotifyIcon、中央結果視窗、設定畫面、歷史收盤價查詢／回補進度畫面
YiHeLee.Application     排程流程、盤中基準準備、驗證、均線策略、官方價格協調（MarketPriceService）、歷史回補並行協調（StockHistoryImportService）、鉅亨交叉驗證、介面
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

## 版本、圖示與 Git Commit 可追溯性（2026-07-13 更新）

啟動時會將程式版本、Git Commit SHA、Git 分支、Build 時間、執行檔完整路徑、SQLite 完整路徑、Excel 完整路徑寫入 Log，主視窗標題列也會顯示簡短版本與 Commit SHA（例如 `Yi He Lee － V2.0 (a1b2c3d4e5f6)`），方便確認目前啟動的不是舊資料夾裡的舊 EXE。正式發佈請使用 `scripts\publish-win-x64.ps1`，會在輸出資料夾另外產生 `buildinfo.json`；直接以 `dotnet build`／`dotnet run` 執行（沒有 `buildinfo.json`）時會明確標示「未透過 publish 腳本發佈」。V2.0 起內建新版系統匣／視窗圖示，設定頁「程式圖示」可指定自訂 `.ico` 或圖片路徑；留空或路徑失效時使用內建圖示。

## 權限設計

Manifest 預設使用 `asInvoker`，確保能連接一般權限啟動的 Excel。系統匣選單提供「以系統管理員重新啟動」。提升後，Excel 也必須用系統管理員權限開啟，否則 Windows UIPI／COM 權限隔離可能讓程式找不到活頁簿。

## 重要限制

- 目前只有 `CnyesTechnicalAlignment` Provider；新增不同網頁時，需要新增專用 Crawler／Parser。
- 若頁面出現分頁，現行 Provider 會拒絕只寫第一頁，避免保存不完整資料；必須先擴充完整分頁遍歷。
- 本程式只判斷兩個排列清單中實際出現的股票；未出現在兩份完整清單中的持股會列在「無法判斷」分頁，不會猜測或使用昨日資料。
