PRAGMA foreign_keys = ON;

-- 股票主檔：避免每日技術資料重複保存股票名稱。
CREATE TABLE IF NOT EXISTS Stocks (
    StockCode TEXT NOT NULL PRIMARY KEY,            -- 股票代碼
    StockName TEXT NOT NULL,                        -- 股票名稱
    CreatedAt TEXT NOT NULL,                        -- 建立時間（ISO 8601）
    UpdatedAt TEXT NOT NULL                         -- 最後更新時間（ISO 8601）
);

-- 每日技術指標正式資料。
CREATE TABLE IF NOT EXISTS TechnicalIndicatorDaily (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,  -- 流水號
    TradeDate TEXT NOT NULL,                        -- 交易日期（yyyy-MM-dd）
    IndicatorType INTEGER NOT NULL,                 -- 1多頭排列／2空頭排列
    MarketType INTEGER NOT NULL,                    -- 1集中市場／2店頭市場
    StockCode TEXT NOT NULL,                        -- 股票代碼
    ClosePrice NUMERIC NOT NULL,                    -- 收盤價
    MovingAverage5 NUMERIC NOT NULL,                -- 5日均價
    MovingAverage20 NUMERIC NOT NULL,               -- 20日均價
    MovingAverage60 NUMERIC NOT NULL,               -- 60日均價
    MovingAverage120 NUMERIC NOT NULL,              -- 120日均價
    SourceUrl TEXT NOT NULL,                        -- 來源網址
    FetchStartedAt TEXT NOT NULL,                   -- 擷取開始時間
    FetchCompletedAt TEXT NOT NULL,                 -- 擷取完成時間
    CreatedAt TEXT NOT NULL,                        -- 建立時間
    UpdatedAt TEXT NOT NULL,                        -- 最後更新時間
    FOREIGN KEY (StockCode) REFERENCES Stocks(StockCode),
    CONSTRAINT UQ_TechnicalIndicatorDaily UNIQUE (TradeDate, IndicatorType, MarketType, StockCode)
);

-- 排程主紀錄。
CREATE TABLE IF NOT EXISTS JobRuns (
    JobId TEXT NOT NULL PRIMARY KEY,                 -- Job／Batch ID
    TargetDate TEXT NOT NULL,                        -- 目標日期
    TaipeiStartedAt TEXT NOT NULL,                   -- 台北開始時間
    TaipeiCompletedAt TEXT NULL,                     -- 台北完成時間
    AttemptNumber INTEGER NOT NULL,                  -- 當日第幾次嘗試
    Status INTEGER NOT NULL,                         -- 執行狀態
    Outcome INTEGER NULL,                            -- 成功／可重試／不可重試
    Message TEXT NULL,                               -- 結果或錯誤訊息
    CrawledCount INTEGER NOT NULL DEFAULT 0,         -- 擷取總筆數
    HoldingCount INTEGER NOT NULL DEFAULT 0,         -- 有效持股筆數
    AlertCount INTEGER NOT NULL DEFAULT 0,           -- 策略通知筆數
    MissingIndicatorCount INTEGER NOT NULL DEFAULT 0 -- 無技術指標筆數
);
CREATE INDEX IF NOT EXISTS IX_JobRuns_TargetDate ON JobRuns(TargetDate, AttemptNumber);

-- 每個來源／市場的排程細節。
CREATE TABLE IF NOT EXISTS JobRunDetails (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,  -- 流水號
    JobId TEXT NOT NULL,                            -- Job ID
    TargetDate TEXT NOT NULL,                       -- 目標日期
    SourceKey TEXT NOT NULL,                        -- 來源識別碼
    SourceUrl TEXT NOT NULL,                        -- 來源網址
    IndicatorType INTEGER NOT NULL,                 -- 資料類型
    MarketType INTEGER NOT NULL,                    -- 市場別
    PageDate TEXT NULL,                             -- 頁面實際日期
    FetchCount INTEGER NOT NULL DEFAULT 0,          -- 抓取筆數
    StartedAt TEXT NOT NULL,                        -- 開始時間
    CompletedAt TEXT NOT NULL,                      -- 完成時間
    Status TEXT NOT NULL,                           -- 細節狀態
    ErrorMessage TEXT NULL,                         -- 錯誤訊息
    FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
    CONSTRAINT UQ_JobRunDetails UNIQUE (JobId, SourceKey, MarketType)
);

-- 每日 Excel 有效持股快照。
-- 2026-07-11 恢復雙價格判斷：「進場價/平均價」與「現價」是兩個完全獨立的欄位，不得混用或互相代替，
-- 因此新增 EntryAveragePrice／EntryAveragePriceIssue，與既有 CurrentPrice／CurrentPriceIssue 分開保存。
CREATE TABLE IF NOT EXISTS CustomerHoldingSnapshots (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,  -- 流水號
    JobId TEXT NOT NULL,                            -- Job ID
    SnapshotDate TEXT NOT NULL,                     -- 快照日期
    WorkbookPath TEXT NOT NULL,                     -- Excel 完整路徑
    SheetName TEXT NOT NULL,                        -- 客戶頁籤
    CustomerName TEXT NOT NULL,                     -- 客戶姓名
    ExcelRow INTEGER NOT NULL,                      -- 原始 Excel 列號
    StockCode TEXT NOT NULL,                        -- 股票代碼
    StockName TEXT NOT NULL,                        -- 股票名稱
    CurrentPrice NUMERIC NULL,                      -- Excel「現價」欄位（外部 DDE）；無法判讀時為 NULL
    CurrentPriceIssue TEXT NULL,                    -- 現價無效原因（例如 #N/A、空白、0、負數、無法解析文字）
    EntryAveragePrice NUMERIC NULL,                 -- Excel「進場價/平均價」欄位（非 DDE）；無法判讀時為 NULL
    EntryAveragePriceIssue TEXT NULL,               -- 進場價/平均價無效原因（例如空白、0、負數、Excel錯誤值、無法解析文字）
    Quantity NUMERIC NULL,                          -- 張數
    HoldingKey TEXT NOT NULL,                       -- 持股唯一識別
    CreatedAt TEXT NOT NULL,                        -- 建立時間
    FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
    CONSTRAINT UQ_CustomerHoldingSnapshots UNIQUE (SnapshotDate, HoldingKey)
);

-- 每日策略通知與無資料清單。
-- 2026-07-11 恢復雙價格判斷：同一持股可能同時產生「進場價/平均價異常」與「現價異常」兩筆通知，
-- 因此唯一鍵新增 AlertKind，避免兩筆通知因同一組 (TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode)
-- 互相覆蓋；同時新增 EntryAveragePrice／EntryAveragePriceIssue／CurrentPriceIssue 欄位。
CREATE TABLE IF NOT EXISTS StrategyAlerts (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,  -- 流水號
    JobId TEXT NOT NULL,                            -- Job ID
    TradeDate TEXT NOT NULL,                        -- 交易日期
    AlertKind INTEGER NOT NULL,                     -- 1均線觸發／2缺少技術資料／3現價無效（DDE）／4進場價/平均價無效
    WorkbookPath TEXT NOT NULL,                     -- Excel 完整路徑
    SheetName TEXT NOT NULL,                        -- 客戶頁籤
    CustomerName TEXT NOT NULL,                     -- 客戶姓名
    ExcelRow INTEGER NOT NULL,                      -- Excel 原始列號
    StockCode TEXT NOT NULL,                        -- 股票代碼
    StockName TEXT NOT NULL,                        -- 股票名稱
    CurrentPrice NUMERIC NULL,                      -- 判斷當下 Excel「現價」欄位（外部 DDE）；現價無效通知列為 NULL
    CurrentPriceIssue TEXT NULL,                    -- 現價無效原因
    EntryAveragePrice NUMERIC NULL,                 -- 判斷當下 Excel「進場價/平均價」欄位（非 DDE）；無效通知列為 NULL
    EntryAveragePriceIssue TEXT NULL,               -- 進場價/平均價無效原因
    Quantity NUMERIC NULL,                          -- 張數
    ClosePrice NUMERIC NULL,                        -- 收盤價
    MovingAverage5 NUMERIC NULL,                    -- 5日均價
    MovingAverage20 NUMERIC NULL,                   -- 20日均價
    MovingAverage60 NUMERIC NULL,                   -- 60日均價
    MovingAverage120 NUMERIC NULL,                  -- 120日均價
    TriggeredMa5 INTEGER NOT NULL,                  -- 是否觸發5日均價（進場價/平均價與現價需同時達標）
    TriggeredMa20 INTEGER NOT NULL,                 -- 是否觸發20日均價（進場價/平均價與現價需同時達標）
    TriggeredMa120 INTEGER NOT NULL,                -- 是否觸發120日均價（進場價/平均價與現價需同時達標）
    TriggerDescription TEXT NOT NULL,               -- 觸發說明
    MarketType INTEGER NULL,                        -- 市場別
    IndicatorType INTEGER NULL,                     -- 資料類型
    SourceUrl TEXT NULL,                            -- 來源網址
    CreatedAt TEXT NOT NULL,                        -- 建立時間
    UpdatedAt TEXT NOT NULL,                        -- 最後更新時間
    FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
    CONSTRAINT UQ_StrategyAlerts UNIQUE (TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode, AlertKind)
);

-- =====================================================================
-- 以下為官方（TWSE／TPEx）每日收盤價與均線計算相關資料表。
-- 鉅亨網（Stocks／TechnicalIndicatorDaily）僅作多頭／空頭排列清單保存與交叉驗證，
-- 正式均線判斷改由本區塊資料表計算，兩者不得混用。
-- =====================================================================

-- 官方股票主檔（TWSE／TPEx）：避免每日重複保存股票名稱，並記錄上市／上櫃分類。
CREATE TABLE IF NOT EXISTS StockMaster (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    StockCode TEXT NOT NULL UNIQUE,                  -- 股票代碼
    StockName TEXT NOT NULL,                         -- 股票名稱（依官方最新回應更新）
    MarketType INTEGER NOT NULL,                     -- 1上市(TWSE)／2上櫃(TPEx)
    SecurityType TEXT NOT NULL DEFAULT '一般',        -- 證券類型備註（一般股票、ETF等）
    IsActive INTEGER NOT NULL DEFAULT 1,             -- 是否仍在追蹤中
    CreatedAt TEXT NOT NULL,                         -- 建立時間
    UpdatedAt TEXT NOT NULL                          -- 最後更新時間
);

-- 官方每日收盤價：唯一鍵為 StockId + TradeDate，同日重跑會更新既有列而非新增。
-- Open/High/Low/TradeVolume/TradeValue/TransactionCount/PriceChange 為選填欄位：
-- 目前 TWSE／TPEx Provider 僅穩定解析收盤價，其餘欄位保持 NULL，不得以任何方式偽造數值。
-- 既有資料庫升級時由程式以 PRAGMA table_info 檢查後用 ALTER TABLE ADD COLUMN 安全新增這些欄位。
CREATE TABLE IF NOT EXISTS StockDailyPrice (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    StockId INTEGER NOT NULL,                        -- 關聯 StockMaster.Id
    TradeDate TEXT NOT NULL,                         -- 交易日期（yyyy-MM-dd，官方驗證後之正式交易日）
    OpenPrice NUMERIC NULL,                          -- 開盤價（選填）
    HighPrice NUMERIC NULL,                          -- 最高價（選填）
    LowPrice NUMERIC NULL,                           -- 最低價（選填）
    ClosePrice NUMERIC NOT NULL,                     -- 官方收盤價
    TradeVolume NUMERIC NULL,                        -- 成交股數（選填）
    TradeValue NUMERIC NULL,                         -- 成交金額（選填）
    TransactionCount INTEGER NULL,                   -- 成交筆數（選填）
    PriceChange NUMERIC NULL,                        -- 漲跌價差（選填）
    SourceProvider TEXT NOT NULL,                    -- 官方來源：TWSE／TPEx
    SourceUrl TEXT NOT NULL,                         -- 來源網址
    SourceDataDate TEXT NOT NULL,                    -- 來源回報的資料日期（驗證通過後應等於 TradeDate）
    FetchBatchId TEXT NOT NULL,                      -- 對應 OfficialPriceBatch.BatchId
    IsOfficial INTEGER NOT NULL DEFAULT 1,           -- 是否官方資料，本表僅保存官方來源固定為真
    FetchedAt TEXT NOT NULL,                         -- 擷取時間
    CreatedAt TEXT NOT NULL,                         -- 建立時間
    UpdatedAt TEXT NOT NULL,                         -- 最後更新時間
    FOREIGN KEY (StockId) REFERENCES StockMaster(Id),
    CONSTRAINT UQ_StockDailyPrice UNIQUE (StockId, TradeDate)
);
CREATE INDEX IF NOT EXISTS IX_StockDailyPrice_TradeDate ON StockDailyPrice(TradeDate);
CREATE INDEX IF NOT EXISTS IX_StockMaster_MarketType_Code ON StockMaster(MarketType, StockCode);

-- 均線計算結果快取：由本系統依官方收盤價自行計算，不得以鉅亨網數值取代。
CREATE TABLE IF NOT EXISTS StockMovingAverage (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    StockId INTEGER NOT NULL,                        -- 關聯 StockMaster.Id
    TradeDate TEXT NOT NULL,                         -- 交易日期
    ClosePrice NUMERIC NULL,                         -- 當日官方收盤價
    Ma5 NUMERIC NULL,                                -- 5日均線（不足5個有效交易日為 NULL）
    Ma20 NUMERIC NULL,                               -- 20日均線
    Ma60 NUMERIC NULL,                                -- 60日均線
    Ma120 NUMERIC NULL,                               -- 120日均線
    AvailableTradingDayCount INTEGER NOT NULL,        -- 實際可用有效交易日數（逐檔計算，非市場整體）
    CalculationStatus INTEGER NOT NULL,               -- 1正常／2交易日數不足／3當日收盤價缺失／4歷史回補失敗
    LatestAvailableTradeDate TEXT NULL,               -- 最新一筆已知有效收盤價日期
    MissingReason TEXT NULL,                          -- 均線資料不足／回補失敗等缺少原因說明
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (StockId) REFERENCES StockMaster(Id),
    CONSTRAINT UQ_StockMovingAverage UNIQUE (StockId, TradeDate)
);

-- 官方價格批次紀錄：每日排程與歷史回補分開記錄，禁止混用不同日期資料。
CREATE TABLE IF NOT EXISTS OfficialPriceBatch (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    BatchId TEXT NOT NULL UNIQUE,                    -- 批次識別碼
    JobType INTEGER NOT NULL,                        -- 1每日排程(DailyMarketData)／2歷史回補(HistoricalBackfill)
    TargetDate TEXT NOT NULL,                        -- 目標日期
    SourceProvider TEXT NOT NULL,                    -- TWSE／TPEx
    MarketType INTEGER NOT NULL,                     -- 1上市／2上櫃
    SourceDataDate TEXT NULL,                        -- 來源實際回報的資料日期
    FetchStartAt TEXT NOT NULL,                      -- 開始時間
    FetchEndAt TEXT NULL,                             -- 完成時間
    FetchedCount INTEGER NOT NULL DEFAULT 0,          -- 抓取筆數
    InsertedCount INTEGER NOT NULL DEFAULT 0,         -- 新增筆數
    UpdatedCount INTEGER NOT NULL DEFAULT 0,          -- 更新筆數
    SkippedCount INTEGER NOT NULL DEFAULT 0,          -- 略過筆數
    FailedCount INTEGER NOT NULL DEFAULT 0,           -- 失敗筆數
    RetryCount INTEGER NOT NULL DEFAULT 0,            -- 重試次數
    Status INTEGER NOT NULL,                          -- 批次狀態（Pending/Running/NotPublished/Holiday/Succeeded/PartialFailed/Failed/InsufficientHistory）
    ErrorMessage TEXT NULL,                           -- 錯誤訊息
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_OfficialPriceBatch_Target ON OfficialPriceBatch(TargetDate, SourceProvider, JobType);

-- 興櫃歷史回補「已查詢但查無資料」記錄（2026-07-11 新增）。
-- 興櫃官方端點沒有整批市場下載，只能逐檔＋逐月查詢；許多興櫃持股在較早的交易日本來就還沒開始交易，
-- 回補迴圈往回走到那些日期時永遠查不到資料。沒有這張表時，這個「查不到」的結果不會被記住，
-- 導致每次執行都要重新對同一批股票、同一批過去日期再問一次官方來源，白白浪費時間。
-- 一經確認「這一天這檔查無資料」即為歷史事實、不會再改變，記錄下來後，未來執行直接略過即可。
CREATE TABLE IF NOT EXISTS EmergingHistoricalNoDataProbe (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    StockCode TEXT NOT NULL,                         -- 股票代碼
    TradeDate TEXT NOT NULL,                         -- 已查詢並確認查無資料的交易日期
    CheckedAt TEXT NOT NULL,                         -- 查詢時間
    CONSTRAINT UQ_EmergingHistoricalNoDataProbe UNIQUE (StockCode, TradeDate)
);
CREATE INDEX IF NOT EXISTS IX_EmergingHistoricalNoDataProbe_Code_Date ON EmergingHistoricalNoDataProbe(StockCode, TradeDate);

-- 歷史回補已走完整個回看範圍仍不足的持股組合記錄（2026-07-11 新增）。
-- 對近期掛牌、長期停牌或興櫃歷史端點確認查無資料的持股，同一策略日期重跑時不需重新掃描整段歷史。
-- 僅在官方來源沒有 Failed／NotPublished 等暫時性狀態時寫入，避免把可恢復錯誤誤判為永久略過。
CREATE TABLE IF NOT EXISTS HistoricalBackfillExhaustionProbe (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    TargetDate TEXT NOT NULL,                        -- 策略指定日期
    RequiredTradingDays INTEGER NOT NULL,            -- MA120 所需有效交易日數
    MaxLookbackCalendarDays INTEGER NOT NULL,        -- 本次回補最大回看日曆日數
    StockSetKey TEXT NOT NULL,                       -- 上市／上櫃／興櫃持股組合雜湊
    InsufficientSummary TEXT NOT NULL,               -- 仍不足的逐檔摘要，供 Log／畫面追查
    CheckedAt TEXT NOT NULL,                         -- 確認時間
    CONSTRAINT UQ_HistoricalBackfillExhaustionProbe UNIQUE
        (TargetDate, RequiredTradingDays, MaxLookbackCalendarDays, StockSetKey)
);
CREATE INDEX IF NOT EXISTS IX_HistoricalBackfillExhaustionProbe_Target
    ON HistoricalBackfillExhaustionProbe(TargetDate, RequiredTradingDays, MaxLookbackCalendarDays);

-- =====================================================================
-- 以下為「歷史收盤價」查詢畫面／使用者手動「立即回補」相關資料表（2026-07-09 新增）。
-- 一個抓取工作＝一個市場＋一個交易日期；本區塊只記錄使用者觸發批次的進度，
-- 實際抓取／解析／驗證／Upsert 一律重用上方 IMarketPriceService／StockDailyPrice 既有邏輯。
-- =====================================================================

-- 使用者手動觸發的歷史收盤價回補批次：一次「立即回補」對應一筆。
CREATE TABLE IF NOT EXISTS StockPriceImportJob (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,       -- 批次編號
    JobType INTEGER NOT NULL,                             -- 工作類型（固定為2歷史回補）
    RequestedTradingDays INTEGER NOT NULL,                 -- 要求的有效交易日數
    TargetDate TEXT NULL,                                  -- 回溯起算的基準日（觸發當下台北日期）
    TimeZoneId TEXT NOT NULL,                              -- 時區，固定 Asia/Taipei
    TotalTasks INTEGER NOT NULL DEFAULT 0,                 -- 總工作數
    CompletedTasks INTEGER NOT NULL DEFAULT 0,             -- 已完成工作數（含成功／休市／失敗／取消）
    SuccessTasks INTEGER NOT NULL DEFAULT 0,               -- 成功工作數
    FailedTasks INTEGER NOT NULL DEFAULT 0,                -- 失敗工作數
    SkippedTasks INTEGER NOT NULL DEFAULT 0,               -- 略過工作數（休市等合法零筆）
    TotalRows INTEGER NOT NULL DEFAULT 0,                  -- 總資料筆數
    ProcessedRows INTEGER NOT NULL DEFAULT 0,              -- 已處理筆數
    InsertedRows INTEGER NOT NULL DEFAULT 0,               -- 新增筆數
    UpdatedRows INTEGER NOT NULL DEFAULT 0,                -- 更新筆數
    SkippedRows INTEGER NOT NULL DEFAULT 0,                -- 略過筆數
    FailedRows INTEGER NOT NULL DEFAULT 0,                 -- 失敗筆數
    ProgressPercent NUMERIC NOT NULL DEFAULT 0,            -- 整體進度百分比
    Status INTEGER NOT NULL,                               -- 執行狀態
    StartedAt TEXT NULL,                                   -- 開始時間
    CompletedAt TEXT NULL,                                 -- 完成時間
    ErrorMessage TEXT NULL,                                -- 錯誤訊息
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_StockPriceImportJob_Status ON StockPriceImportJob(Status, CreatedAt);

-- 一個抓取工作＝一個市場＋一個交易日期；每一列完整下載、解析、驗證後才會成批寫入 StockDailyPrice。
CREATE TABLE IF NOT EXISTS StockPriceImportTask (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,       -- 工作編號
    JobId INTEGER NOT NULL,                               -- 所屬批次編號
    MarketType INTEGER NOT NULL,                          -- 1上市(TWSE)／2上櫃(TPEx)
    RequestedDate TEXT NOT NULL,                          -- 請求日期
    ActualTradeDate TEXT NULL,                            -- 官方回報的實際交易日期（可能與請求日期不同）
    SourceUrl TEXT NULL,                                  -- 來源網址
    Status INTEGER NOT NULL,                              -- 工作狀態
    RetryCount INTEGER NOT NULL DEFAULT 0,                -- 重試次數
    TotalRows INTEGER NOT NULL DEFAULT 0,                 -- 總筆數
    ProcessedRows INTEGER NOT NULL DEFAULT 0,             -- 已處理筆數
    InsertedRows INTEGER NOT NULL DEFAULT 0,              -- 新增筆數
    UpdatedRows INTEGER NOT NULL DEFAULT 0,               -- 更新筆數
    SkippedRows INTEGER NOT NULL DEFAULT 0,               -- 略過筆數
    FailedRows INTEGER NOT NULL DEFAULT 0,                -- 失敗筆數
    ProgressPercent NUMERIC NOT NULL DEFAULT 0,           -- 工作進度百分比
    StartedAt TEXT NULL,                                  -- 開始時間
    CompletedAt TEXT NULL,                                -- 完成時間
    ErrorMessage TEXT NULL,                               -- 錯誤訊息
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (JobId) REFERENCES StockPriceImportJob(Id),
    CONSTRAINT UQ_StockPriceImportTask UNIQUE (JobId, MarketType, RequestedDate)
);
CREATE INDEX IF NOT EXISTS IX_StockPriceImportTask_Job_Status ON StockPriceImportTask(JobId, Status);

-- 鉅亨網多頭／空頭排列與官方自算均線的交叉驗證紀錄；僅作驗證追查，不得覆蓋或取代官方資料。
CREATE TABLE IF NOT EXISTS CnyesCrossValidation (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    TradeDate TEXT NOT NULL,                         -- 交易日期
    MarketType INTEGER NOT NULL,                     -- 1上市／2上櫃
    StockCode TEXT NOT NULL,                         -- 股票代碼
    WindowDays INTEGER NOT NULL,                     -- 比對的均線天數（5／20／60／120；0表示非特定天數的整體狀態）
    CalculatedValue NUMERIC NULL,                    -- 本系統依官方收盤價自算的均價
    CnyesValue NUMERIC NULL,                         -- 鉅亨網頁面顯示的均價
    Difference NUMERIC NULL,                         -- 絕對差異
    Outcome INTEGER NOT NULL,                        -- 驗證結果（相符／差異／不適用／日期不符／資料不足／來源失敗）
    CnyesDataDate TEXT NULL,                         -- 鉅亨網頁面實際顯示的資料日期
    SourceUrl TEXT NULL,                             -- 鉅亨網來源網址
    ValidatedAt TEXT NOT NULL,                       -- 驗證時間
    ErrorMessage TEXT NULL,                          -- 錯誤訊息
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CONSTRAINT UQ_CnyesCrossValidation UNIQUE (TradeDate, MarketType, StockCode, WindowDays)
);
CREATE INDEX IF NOT EXISTS IX_CnyesCrossValidation_TradeDate ON CnyesCrossValidation(TradeDate);
