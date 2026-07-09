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
