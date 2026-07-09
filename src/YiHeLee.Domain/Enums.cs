namespace YiHeLee.Domain;

public enum IndicatorType
{
    BullishAlignment = 1,
    BearishAlignment = 2
}

public enum MarketType
{
    Listed = 1,
    Otc = 2
}

public enum JobStatus
{
    Running = 1,
    Succeeded = 2,
    WebsiteNotUpdated = 3,
    NoTradingData = 4,
    CrawlFailed = 5,
    ValidationFailed = 6,
    ExcelUnavailable = 7,
    ExcelWriteFailed = 8,
    Cancelled = 9
}

public enum RunOutcome
{
    Success = 1,
    RetryableFailure = 2,
    NonRetryableFailure = 3
}

public enum AlertKind
{
    MovingAverageTriggered = 1,
    TechnicalIndicatorMissing = 2
}
