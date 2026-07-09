using YiHeLee.Application.Abstractions;

namespace YiHeLee.Infrastructure.Crawlers;

public sealed class CrawlerRegistry : ICrawlerRegistry
{
    private readonly IReadOnlyDictionary<string, ISourceCrawler> _crawlers;

    public CrawlerRegistry(IEnumerable<ISourceCrawler> crawlers)
    {
        _crawlers = crawlers.ToDictionary(x => x.ProviderKey, StringComparer.OrdinalIgnoreCase);
    }

    public ISourceCrawler Resolve(string providerKey)
    {
        if (!_crawlers.TryGetValue(providerKey, out var crawler))
        {
            throw new KeyNotFoundException($"找不到 ProviderKey={providerKey} 的爬蟲。");
        }

        return crawler;
    }
}
