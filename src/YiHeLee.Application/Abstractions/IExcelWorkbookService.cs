using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

public interface IExcelWorkbookService
{
    Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
        AppSettings settings,
        DateOnly targetDate,
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null);

    Task WriteStrategyResultsAsync(
        AppSettings settings,
        DateOnly targetDate,
        IReadOnlyList<StrategyAlert> alerts,
        CancellationToken cancellationToken);
}
