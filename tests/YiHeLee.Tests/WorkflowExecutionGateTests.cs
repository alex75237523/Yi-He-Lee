using YiHeLee.Application.Services;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證流程協調鎖（2026-07-13 盤中／收盤流程拆分）：同一時間只允許一個流程持有；
/// 盤中 Tick 以 TryEnter 嘗試、被占用時不排隊；收盤更新以 EnterAsync 等待進入。
/// </summary>
public sealed class WorkflowExecutionGateTests
{
    [Fact]
    public void 同一時間只能有一個持有者_釋放後可再進入()
    {
        var gate = new WorkflowExecutionGate();

        var first = gate.TryEnter("盤中判斷");
        Assert.NotNull(first);
        Assert.Equal("盤中判斷", gate.CurrentOwner);
        Assert.Null(gate.TryEnter("盤中判斷"));

        first!.Dispose();
        Assert.Null(gate.CurrentOwner);

        using var second = gate.TryEnter("收盤更新");
        Assert.NotNull(second);
        Assert.Equal("收盤更新", gate.CurrentOwner);
    }

    [Fact]
    public async Task 收盤更新等待盤中判斷結束後才進入_期間盤中Tick被擋下()
    {
        var gate = new WorkflowExecutionGate();
        var intradayTicket = gate.TryEnter("盤中判斷");
        Assert.NotNull(intradayTicket);

        var closeEnterTask = gate.EnterAsync("收盤更新", CancellationToken.None);
        Assert.False(closeEnterTask.IsCompleted);

        // 盤中判斷結束、釋放鎖後，收盤更新才取得鎖；期間新的盤中 Tick TryEnter 一律失敗。
        intradayTicket!.Dispose();
        using var closeTicket = await closeEnterTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("收盤更新", gate.CurrentOwner);
        Assert.Null(gate.TryEnter("盤中判斷"));
    }

    [Fact]
    public void 重複釋放不影響鎖狀態()
    {
        var gate = new WorkflowExecutionGate();
        var ticket = gate.TryEnter("盤中判斷");
        ticket!.Dispose();
        ticket.Dispose(); // 第二次 Dispose 應為無動作，不得多釋放一次號誌。

        Assert.NotNull(gate.TryEnter("盤中判斷"));
        Assert.Null(gate.TryEnter("收盤更新"));
    }
}
