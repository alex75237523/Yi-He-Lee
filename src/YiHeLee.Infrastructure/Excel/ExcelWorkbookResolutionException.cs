namespace YiHeLee.Infrastructure.Excel;

/// <summary>代表連接既有 Excel 活頁簿時的可分類錯誤。</summary>
internal sealed class ExcelWorkbookResolutionException : Exception
{
    public ExcelWorkbookResolutionException(
        string message,
        bool isRetryable,
        string diagnosticMessage,
        Exception? innerException = null)
        : base(message, innerException)
    {
        IsRetryable = isRetryable;
        DiagnosticMessage = diagnosticMessage;
    }

    public bool IsRetryable { get; }

    public string DiagnosticMessage { get; }
}
