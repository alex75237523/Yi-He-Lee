namespace YiHeLee.Infrastructure.Excel;

/// <summary>集中處理 Excel 回報路徑與設定路徑的正規化及比對。</summary>
internal static class ExcelWorkbookPathMatcher
{
    public static string NormalizeConfiguredPath(string workbookPath)
    {
        if (string.IsNullOrWhiteSpace(workbookPath))
        {
            throw new ArgumentException("尚未設定 Excel 活頁簿路徑。", nameof(workbookPath));
        }

        return NormalizePath(workbookPath)
               ?? throw new ArgumentException($"Excel 活頁簿路徑格式無效：{workbookPath}", nameof(workbookPath));
    }

    public static bool IsSamePath(string? candidate, string configuredFullPath)
    {
        var normalizedCandidate = NormalizePath(candidate);
        var normalizedConfigured = NormalizePath(configuredFullPath);
        return normalizedCandidate is not null
               && normalizedConfigured is not null
               && string.Equals(normalizedCandidate, normalizedConfigured, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasSameFileName(string? candidate, string configuredFullPath)
    {
        var candidateName = GetFileName(candidate);
        var configuredName = GetFileName(configuredFullPath);
        return !string.IsNullOrWhiteSpace(candidateName)
               && !string.IsNullOrWhiteSpace(configuredName)
               && string.Equals(candidateName, configuredName, StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return uri.AbsoluteUri.TrimEnd('/');
            }

            value = uri.LocalPath;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch
        {
            return null;
        }
    }

    public static string GetFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(path.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        }

        try
        {
            return Path.GetFileName(path.Trim().Trim('"'));
        }
        catch
        {
            return string.Empty;
        }
    }
}
