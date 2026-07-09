using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

public interface ISourceCrawler
{
    string ProviderKey { get; }
    Task<CrawlBatch> CrawlAsync(
        SourceDefinition source,
        MarketType marketType,
        DateOnly targetDate,
        AppSettings settings,
        CancellationToken cancellationToken);
}

public interface ICrawlerRegistry
{
    ISourceCrawler Resolve(string providerKey);
}
