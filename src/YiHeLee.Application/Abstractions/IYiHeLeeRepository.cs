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
}
