# settings.json 欄位說明

設定檔位置：**程式 exe 所在資料夾**的 `settings.json`（可攜式設計，整個資料夾複製到別台電腦即可沿用）。

注意事項：

- JSON 格式**不支援註解**，請勿在 `settings.json` 內加上 `//` 或 `/* */`，否則程式啟動會讀取失敗。
- 大部分欄位都可以在程式的「設定」頁籤修改，儲存時程式會**重寫整份檔案**；若要手動編輯，請先從系統匣「結束」程式再改。
- 檔案內中文會以 `\uXXXX` 形式儲存（例如 `總表` = 總表），屬正常現象，程式讀取時會自動還原。
- 檔案中**缺少的欄位**會自動採用預設值，不會報錯。

## 主要欄位

| 欄位 | 預設值 | 說明 |
|---|---|---|
| `WorkbookPath` | （空） | 客戶持股 Excel 檔完整路徑（親帶績效檔）。 |
| `OutputWorksheetName` | `每日五日均價策略` | 判斷結果要寫入的輸出頁籤名稱。 |
| `AppIconPath` | （空） | 自訂程式圖示路徑；留空使用內建 V1.3 圖示。支援 `.ico`、`.png`、`.jpg`、`.jpeg`、`.bmp`，可在設定頁「程式圖示」輸入或選檔。路徑失效時會自動退回內建圖示，不影響主流程。 |
| `ExcludedWorksheetNames` | `總表`、`每日五日均價策略` | 讀取持股時**跳過**的頁籤（非客戶持股頁）。 |
| `DailyRunTime` | `13:35:00` | 每日自動執行時間。**固定台北時間 13:35，改了也會被程式校正回來**。 |
| `RetryIntervalMinutes` | `10` | 網站尚未更新／Excel 無法使用時，長時間重試的間隔（分鐘）。 |
| `MaximumDailyAttempts` | `12` | 每日最多執行次數（含重試），超過即當日放棄。 |
| `CrawlerShortRetryCount` | `3` | 每次爬蟲失敗時的短暫重試次數。 |
| `CrawlerShortRetryDelaySeconds` | `5` | 爬蟲短暫重試之間的等待秒數。 |
| `ExcelShortRetryCount` | `5` | Excel 忙碌（儲存格編輯中、對話框開啟）時的短暫重試次數。 |
| `ExcelShortRetryDelaySeconds` | `2` | Excel 忙碌重試之間的等待秒數。 |
| `StartWithWindows` | `true` | 登入 Windows 後自動啟動。 |
| `StartMinimized` | `true` | 啟動後只顯示在右下角系統匣，不開主視窗。 |
| `RequireBackupBeforeExcelWrite` | `true` | 寫入 Excel 前先在 `Backups` 資料夾建立備份。 |
| `CreateOutputWorksheetIfMissing` | `true` | 找不到輸出頁籤時自動建立。 |
| `ShowExcelSafetyPrompt` | `true` | 操作 Excel 前在「操作」頁顯示防呆確認（開始／取消）。 |
| `AutoOpenWorkbookIfClosed` | `true` | 找不到已開啟的活頁簿時，自動用 Excel 開啟該檔案。 |
| `EnableDailySchedule` | `true` | 每日 13:35 自動執行排程是否啟用；設為 `false` 時排程停用，使用者仍可手動點選「立即執行」。 |
| `ShowHistoricalPriceButton` | `true` | 是否顯示「歷史收盤價」按鈕與系統匣選單項目；設為 `false` 可隱藏。**只能改檔案，設定頁籤沒有此選項**。 |
| `ShowStatusText` | `true` | 「操作」頁是否顯示執行中的文字狀態（目前執行到哪個步驟）；設為 `false` 時只顯示進度條。**只能改檔案**。註：MA120 歷史回補的逐日細節進度不受此旗標影響，一律顯示於進度條下方。 |
| `ShowSourceSettings` | `false` | 設定頁是否顯示「資料來源網址」頁籤（鉅亨網來源清單）。預設隱藏避免誤改；隱藏時來源設定仍原樣保留、正常運作。**只能改檔案**。 |
| `ExcludedHoldingFillColors` | `#92D050` | 股名儲存格套用這些填滿色 = 人工標記「不判斷」，整列跳過。 |
| `ExcludedHoldingTextMarkers` | `不判斷`、`已出場` 等 | 持股列出現這些文字時整列跳過。 |

## Sources（鉅亨網資料來源清單）

固定兩個鉅亨網來源（多頭／空頭排列）**不可停用或刪除**，程式會自動補回；可自行新增其他來源。

| 欄位 | 說明 |
|---|---|
| `SourceKey` | 來源唯一代碼（自訂來源會自動產生 `CUSTOM_...`）。 |
| `DisplayName` | 顯示名稱。 |
| `Url` | 來源網址，必須是 `https`。 |
| `IndicatorType` | `BullishAlignment`（多頭排列）或 `BearishAlignment`（空頭排列）。 |
| `ProviderKey` | 對應的爬蟲實作；不同 HTML 結構的網站需要另外開發，目前僅有 `CnyesTechnicalAlignment`。 |
| `Enabled` | 是否啟用。 |
| `Required` | `true` = 固定來源（不可刪）。 |

## OfficialMarketData（TWSE／TPEx 官方收盤價與均線）

| 欄位 | 預設值 | 說明 |
|---|---|---|
| `TwseDailyCloseUrlTemplate` | TWSE 官方端點 | 上市每日收盤行情網址樣板，`{0}` 置換為 `yyyyMMdd`。 |
| `TpexDailyCloseUrlTemplate` | TPEx 官方端點 | 上櫃每日收盤行情網址樣板，`{0}` 置換為民國年/月/日，例如 `115/07/08`。 |
| `EmergingDailyCloseUrl` | TPEx OpenAPI | 興櫃當日行情（即時快照，無日期參數）。 |
| `EmergingHistoricalUrlTemplate` | TPEx 官方端點 | 興櫃個股歷史行情網址樣板，`{0}` 置換為民國年月（例如 `115/07`），`{1}` 置換為股票代碼；每日自動回補只針對 Excel 持股中的興櫃股票使用。 |
| `HttpTimeoutSeconds` | `30` | 單次 HTTP 請求逾時秒數。 |
| `HttpShortRetryCount` | `3` | HTTP 失敗短暫重試次數。 |
| `HttpShortRetryDelaySeconds` | `5` | HTTP 重試等待秒數。 |
| `RequiredTradingDaysForMa120` | `120` | 計算 MA120 需要的最少有效交易日數。 |
| `MaxBackfillLookbackCalendarDays` | `280` | 歷史回補最多往前回看的日曆天數。 |
| `BackfillThrottleMillisecondsBetweenRequests` | `300` | 回補逐日請求之間的節流間隔（毫秒），避免轟炸官方網站。 |

注意：「頁面日期必須等於當日」「不得回抓前一交易日頂替」為固定規則，**無法**透過任何設定關閉。

## StockHistoryImport（歷史收盤價手動回補）

| 欄位 | 預設值 | 說明 |
|---|---|---|
| `DefaultTradingDays` | `5` | 「立即回補」預設回補的有效交易日數。 |
| `MaxSelectableTradingDays` | `250` | 畫面上可選擇的最大交易日數。 |
| `MaxConcurrency` | `4` | 同時並行的抓取工作數（規範上限 8）。 |
| `RequestTimeoutSeconds` | `30` | 單次請求逾時秒數。 |
| `MaxRetryCount` | `3` | 暫時性錯誤（408／429／5xx、逾時）最大重試次數。 |
| `RetryBaseDelaySeconds` | `2` | 指數退避基礎秒數（2、4、8 秒遞增）。 |
