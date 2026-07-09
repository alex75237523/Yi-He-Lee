namespace YiHeLee.Application.Abstractions;

public interface IClock
{
    DateTimeOffset GetTaipeiNow();
    DateOnly GetTaipeiToday();
}
