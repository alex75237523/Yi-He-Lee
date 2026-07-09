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
    EntryAveragePrice NUMERIC NOT NULL,              -- 進場價／平均價
    Quantity NUMERIC NULL,                          -- 張數
    HoldingKey TEXT NOT NULL,                       -- 持股唯一識別
    CreatedAt TEXT NOT NULL,                        -- 建立時間
    FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
    CONSTRAINT UQ_CustomerHoldingSnapshots UNIQUE (SnapshotDate, HoldingKey)
);

-- 每日策略通知與無資料清單。
CREATE TABLE IF NOT EXISTS StrategyAlerts (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,  -- 流水號
    JobId TEXT NOT NULL,                            -- Job ID
    TradeDate TEXT NOT NULL,                        -- 交易日期
    AlertKind INTEGER NOT NULL,                     -- 1均線觸發／2缺少技術資料
    WorkbookPath TEXT NOT NULL,                     -- Excel 完整路徑
    SheetName TEXT NOT NULL,                        -- 客戶頁籤
    CustomerName TEXT NOT NULL,                     -- 客戶姓名
    ExcelRow INTEGER NOT NULL,                      -- Excel 原始列號
    StockCode TEXT NOT NULL,                        -- 股票代碼
    StockName TEXT NOT NULL,                        -- 股票名稱
    EntryAveragePrice NUMERIC NOT NULL,              -- 進場價／平均價
    Quantity NUMERIC NULL,                          -- 張數
    ClosePrice NUMERIC NULL,                        -- 收盤價
    MovingAverage5 NUMERIC NULL,                    -- 5日均價
    MovingAverage20 NUMERIC NULL,                   -- 20日均價
    MovingAverage60 NUMERIC NULL,                   -- 60日均價
    MovingAverage120 NUMERIC NULL,                  -- 120日均價
    TriggeredMa5 INTEGER NOT NULL,                  -- 是否觸發5日均價
    TriggeredMa20 INTEGER NOT NULL,                 -- 是否觸發20日均價
    TriggeredMa120 INTEGER NOT NULL,                -- 是否觸發120日均價
    TriggerDescription TEXT NOT NULL,               -- 觸發說明
    MarketType INTEGER NULL,                        -- 市場別
    IndicatorType INTEGER NULL,                     -- 資料類型
    SourceUrl TEXT NULL,                            -- 來源網址
    CreatedAt TEXT NOT NULL,                        -- 建立時間
    UpdatedAt TEXT NOT NULL,                        -- 最後更新時間
    FOREIGN KEY (JobId) REFERENCES JobRuns(JobId),
    CONSTRAINT UQ_StrategyAlerts UNIQUE (TradeDate, WorkbookPath, SheetName, ExcelRow, StockCode)
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
CREATE TABLE IF NOT EXISTS StockDailyPrice (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,   -- 流水號
    StockId INTEGER NOT NULL,                        -- 關聯 StockMaster.Id
    TradeDate TEXT NOT NULL,                         -- 交易日期（yyyy-MM-dd，官方驗證後之正式交易日）
    ClosePrice NUMERIC NOT NULL,                     -- 官方收盤價
    SourceProvider TEXT NOT NULL,                    -- 官方來源：TWSE／TPEx
    SourceUrl TEXT NOT NULL,                         -- 來源網址
    SourceDataDate TEXT NOT NULL,                    -- 來源回報的資料日期（驗證通過後應等於 TradeDate）
    FetchBatchId TEXT NOT NULL,                      -- 對應 OfficialPriceBatch.BatchId
    FetchedAt TEXT NOT NULL,                         -- 擷取時間
    CreatedAt TEXT NOT NULL,                         -- 建立時間
    UpdatedAt TEXT NOT NULL,                         -- 最後更新時間
    FOREIGN KEY (StockId) REFERENCES StockMaster(Id),
    CONSTRAINT UQ_StockDailyPrice UNIQUE (StockId, TradeDate)
);
CREATE INDEX IF NOT EXISTS IX_StockDailyPrice_TradeDate ON StockDailyPrice(TradeDate);

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
    AvailableTradingDayCount INTEGER NOT NULL,        -- 實際可用有效交易日數
    CalculationStatus INTEGER NOT NULL,               -- 1正常／2交易日數不足
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
