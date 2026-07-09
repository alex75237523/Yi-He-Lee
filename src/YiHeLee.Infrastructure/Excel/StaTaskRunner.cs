namespace YiHeLee.Infrastructure.Excel;

internal static class StaTaskRunner
{
    public static Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                OleMessageFilter.Register();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                OleMessageFilter.Revoke();
            }
        })
        {
            IsBackground = true,
            Name = "YiHeLee.Excel.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
