using YiHeLee.Application.Abstractions;

namespace YiHeLee.Infrastructure.Time;

public sealed class TaipeiClock : IClock
{
    private readonly TimeZoneInfo _taipeiTimeZone;

    public TaipeiClock()
    {
        _taipeiTimeZone = ResolveTaipeiTimeZone();
    }

    public DateTimeOffset GetTaipeiNow()
    {
        var utcNow = DateTimeOffset.UtcNow;
        return TimeZoneInfo.ConvertTime(utcNow, _taipeiTimeZone);
    }

    public DateOnly GetTaipeiToday() => DateOnly.FromDateTime(GetTaipeiNow().DateTime);

    private static TimeZoneInfo ResolveTaipeiTimeZone()
    {
        // Windows 使用 Taipei Standard Time；IANA 環境使用 Asia/Taipei。
        foreach (var id in new[] { "Taipei Standard Time", "Asia/Taipei" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new InvalidOperationException("無法取得 Asia/Taipei 時區。");
    }
}
