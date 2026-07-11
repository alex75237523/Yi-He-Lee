using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

public interface IYiHeLeeRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Guid> BeginJobAsync(DateOnly targetDate, int attemptNumber, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task RecordJobDetailAsync(Guid jobId, CrawlBatch batch, string status, string? errorMessage, CancellationToken cancellationToken);
    Task RecordJobDetailFailureAsync(Guid jobId, SourceDefinition source, MarketType marketType, DateOnly targetDate, string status, string errorMessage, DateTimeOffset startedAt, DateTimeOffset completedAt, CancellationToken cancellationToken);
    Task SaveCompleteTechnicalBatchAsync(Guid jobId, IReadOnlyList<CrawlBatch> batches, CancellationToken cancellationToken);
    Task<IReadOnlyList<TechnicalIndicator>> GetTechnicalIndicatorsAsync(DateOnly tradeDate, CancellationToken cancellationToken);
    Task SaveHoldingsAndAlertsAsync(
        Guid jobId,
        DateOnly tradeDate,
        string workbookPath,
        IReadOnlyList<CustomerHolding> holdings,
        IReadOnlyList<StrategyAlert> alerts,
        CancellationToken cancellationToken);
    Task CompleteJobAsync(Guid jobId, JobRunSummary summary, CancellationToken cancellationToken);
    Task<int> GetAttemptCountAsync(DateOnly targetDate, CancellationToken cancellationToken);
    Task<JobRunSummary?> GetLatestJobSummaryAsync(CancellationToken cancellationToken);

    /// <summary>取得指定目標日期的最新一筆工作。排程器判斷「今日是否已處理」必須用本方法，
    /// 不得用 <see cref="GetLatestJobSummaryAsync"/>：手動回溯執行過去日期後，最新一筆會變成過去日期，
    /// 會被誤判為「今日尚未執行」而重跑今日並蓋掉使用者正在查看的結果畫面。</summary>
    Task<JobRunSummary?> GetLatestJobSummaryForDateAsync(DateOnly targetDate, CancellationToken cancellationToken);
}
