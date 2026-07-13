namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 盤中判斷與收盤更新的共用流程協調鎖（2026-07-13 盤中／收盤流程拆分新增）。
/// 規則：同一時間只能有一個盤中判斷；收盤更新執行時不得啟動盤中判斷；
/// Excel COM 操作不得同時由兩個流程執行。
/// </summary>
public interface IWorkflowExecutionGate
{
    /// <summary>目前持有鎖的流程名稱；無人持有時為 null。供略過原因顯示。</summary>
    string? CurrentOwner { get; }

    /// <summary>嘗試立即取得鎖；已被占用時回傳 null，呼叫端（盤中 Tick）直接略過本次，不得排隊等待。</summary>
    IDisposable? TryEnter(string ownerName);

    /// <summary>等待取得鎖（收盤更新使用：等待目前盤中判斷結束後開始，期間新的盤中 Tick 會被 TryEnter 擋下）。</summary>
    Task<IDisposable> EnterAsync(string ownerName, CancellationToken cancellationToken);
}
