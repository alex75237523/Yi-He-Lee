using YiHeLee.Application.Abstractions;

namespace YiHeLee.Application.Services;

/// <summary>
/// 盤中判斷與收盤更新的共用流程協調鎖（2026-07-13 盤中／收盤流程拆分新增）。
/// 同一時間只允許一個流程持有：盤中 Tick 以 <see cref="TryEnter"/> 嘗試進入，
/// 已被占用時直接略過本次、不排隊；收盤更新以 <see cref="EnterAsync"/> 等待進入，
/// 期間新的盤中 Tick 全部被擋下，確保 Excel COM 操作不會同時由兩個流程執行。
/// </summary>
public sealed class WorkflowExecutionGate : IWorkflowExecutionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile string? _currentOwner;

    public string? CurrentOwner => _currentOwner;

    public IDisposable? TryEnter(string ownerName)
    {
        if (!_semaphore.Wait(0))
        {
            return null;
        }

        _currentOwner = ownerName;
        return new Releaser(this);
    }

    public async Task<IDisposable> EnterAsync(string ownerName, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        _currentOwner = ownerName;
        return new Releaser(this);
    }

    private void Release()
    {
        _currentOwner = null;
        _semaphore.Release();
    }

    private sealed class Releaser(WorkflowExecutionGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
