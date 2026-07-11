using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

public sealed class SettingsValidationService
{
    private static readonly string[] RequiredSourceKeys =
    [
        "CNYES_BULLISH_ALIGNMENT",
        "CNYES_BEARISH_ALIGNMENT"
    ];

    public IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.WorkbookPath))
        {
            errors.Add("尚未設定 Excel 檔案路徑。");
        }
        else if (!File.Exists(settings.WorkbookPath))
        {
            errors.Add($"找不到 Excel 檔案：{settings.WorkbookPath}");
        }
        else if (!string.Equals(Path.GetExtension(settings.WorkbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(Path.GetExtension(settings.WorkbookPath), ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Excel 檔案只支援 .xlsx 或 .xlsm。");
        }

        if (string.IsNullOrWhiteSpace(settings.OutputWorksheetName))
        {
            errors.Add("輸出頁籤名稱不可空白。");
        }

        if (settings.DailyRunTime != AppSettings.FixedDailyRunTime)
        {
            errors.Add("固定執行時間必須是 Asia/Taipei 13:35。");
        }

        if (settings.RetryIntervalMinutes < 1)
        {
            errors.Add("長時間重試間隔至少 1 分鐘。");
        }

        if (settings.MaximumDailyAttempts < 1)
        {
            errors.Add("每日最大執行次數至少為 1。");
        }

        if (settings.CrawlerShortRetryCount < 1 || settings.ExcelShortRetryCount < 1)
        {
            errors.Add("爬蟲與 Excel 短暫重試次數至少為 1。");
        }

        var duplicateSourceKeys = settings.Sources
            .Where(x => x.Enabled)
            .GroupBy(x => x.SourceKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1)
            .Select(x => x.Key);
        foreach (var duplicate in duplicateSourceKeys)
        {
            errors.Add(string.IsNullOrWhiteSpace(duplicate) ? "來源識別碼不可空白。" : $"來源識別碼重複：{duplicate}");
        }

        var duplicateUrls = settings.Sources
            .Where(x => x.Enabled)
            .GroupBy(x => x.Url.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key);
        foreach (var duplicate in duplicateUrls)
        {
            errors.Add($"來源網址重複：{duplicate}");
        }

        foreach (var source in settings.Sources.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(source.DisplayName))
            {
                errors.Add($"來源 {source.SourceKey} 的顯示名稱不可空白。");
            }

            if (string.IsNullOrWhiteSpace(source.ProviderKey))
            {
                errors.Add($"來源 {source.SourceKey} 的 ProviderKey 不可空白。");
            }

            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"來源網址必須是有效的 HTTPS：{source.Url}");
            }
        }

        foreach (var requiredKey in RequiredSourceKeys)
        {
            var source = settings.Sources.FirstOrDefault(x => string.Equals(x.SourceKey, requiredKey, StringComparison.OrdinalIgnoreCase));
            if (source is null || !source.Enabled || !source.Required)
            {
                errors.Add($"固定來源 {requiredKey} 不得刪除或停用。");
            }
        }

        foreach (var color in settings.ExcludedHoldingFillColors)
        {
            if (!TryNormalizeColor(color, out _))
            {
                errors.Add($"不判斷填滿色格式錯誤：{color}，請使用 #RRGGBB。");
            }
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void EnsureFixedSources(AppSettings settings)
    {
        var officialDefaults = new OfficialMarketDataSettings();
        settings.Sources ??= [];
        settings.OfficialMarketData ??= officialDefaults;
        settings.ExcludedWorksheetNames ??= [];
        settings.ExcludedHoldingFillColors ??= [];
        settings.ExcludedHoldingTextMarkers ??= [];

        // 固定排程不得由設定檔改成其他時間。
        settings.DailyRunTime = AppSettings.FixedDailyRunTime;

        // 官方收盤價是正式均線來源，端點不得停留在舊版或被使用者誤改；
        // 舊 TPEx 端點會在歷史日期查詢時靜默回傳最新交易日，導致上櫃 MA 無法補足。
        settings.OfficialMarketData.TwseDailyCloseUrlTemplate = officialDefaults.TwseDailyCloseUrlTemplate;
        settings.OfficialMarketData.TpexDailyCloseUrlTemplate = officialDefaults.TpexDailyCloseUrlTemplate;
        settings.OfficialMarketData.EmergingDailyCloseUrl = officialDefaults.EmergingDailyCloseUrl;
        settings.OfficialMarketData.EmergingHistoricalUrlTemplate = officialDefaults.EmergingHistoricalUrlTemplate;

        foreach (var required in SourceDefinitionSetting.CreateDefaults())
        {
            var existing = settings.Sources.FirstOrDefault(x => string.Equals(x.SourceKey, required.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                settings.Sources.Add(required);
                continue;
            }

            // 固定來源的網址、資料類型與必要性不可由使用者關閉。
            existing.DisplayName = required.DisplayName;
            existing.Url = required.Url;
            existing.IndicatorType = required.IndicatorType;
            existing.ProviderKey = required.ProviderKey;
            existing.Enabled = true;
            existing.Required = true;
        }

        settings.ExcludedWorksheetNames = settings.ExcludedWorksheetNames
            .Concat(["總表", settings.OutputWorksheetName])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.ExcludedHoldingFillColors = settings.ExcludedHoldingFillColors
            .Select(x => TryNormalizeColor(x, out var normalized) ? normalized : x.Trim())
            .Append("#92D050")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.ExcludedHoldingTextMarkers = settings.ExcludedHoldingTextMarkers
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimStart('#').ToUpperInvariant();
        if (text.Length != 6 || text.Any(ch => !Uri.IsHexDigit(ch)))
        {
            return false;
        }

        normalized = "#" + text;
        return true;
    }
}
