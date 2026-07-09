using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

public interface IExcelWorkbookService
{
    Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
        AppSettings settings,
        DateOnly targetDate,
        CancellationToken cancellationToken);

    Task WriteStrategyResultsAsync(
        AppSettings settings,
        DateOnly targetDate,
        IReadOnlyList<StrategyAlert> alerts,
        CancellationToken cancellationToken);
}
