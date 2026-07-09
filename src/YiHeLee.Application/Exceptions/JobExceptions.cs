using YiHeLee.Domain;

namespace YiHeLee.Application.Exceptions;

public class RetryableJobException : Exception
{
    public RetryableJobException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public class NonRetryableJobException : Exception
{
    public NonRetryableJobException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class WebsiteNotUpdatedException : RetryableJobException
{
    public WebsiteNotUpdatedException(string message) : base(message) { }
}

/// <summary>Excel 暫時性問題，例如忙碌、正在編輯、對話框阻擋或活頁簿尚未開啟。</summary>
public sealed class RetryableExcelJobException : RetryableJobException
{
    public RetryableExcelJobException(JobStatus status, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
    }

    public JobStatus Status { get; }
}

/// <summary>Excel 必須由使用者修正後才能再執行的問題，例如唯讀、工作表保護或設定錯誤。</summary>
public sealed class NonRetryableExcelJobException : NonRetryableJobException
{
    public NonRetryableExcelJobException(JobStatus status, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
    }

    public JobStatus Status { get; }
}
