namespace YiHeLee.Domain;

/// <summary>Yi He Lee 使用者設定。固定資料來源與固定排程時間會由 Application 層再次校正。</summary>
public sealed class AppSettings
{
    public static readonly TimeOnly FixedDailyRunTime = new(13, 35);

    public string WorkbookPath { get; set; } = string.Empty;
    public string OutputWorksheetName { get; set; } = "每日五日均價策略";
    public List<string> ExcludedWorksheetNames { get; set; } = ["總表", "每日五日均價策略"];

    /// <summary>依專案固定規範，實際儲存時一律校正為台北時間 13:35。</summary>
    public TimeOnly DailyRunTime { get; set; } = FixedDailyRunTime;

    public int RetryIntervalMinutes { get; set; } = 10;
    public int MaximumDailyAttempts { get; set; } = 12;
    public int CrawlerShortRetryCount { get; set; } = 3;
    public int CrawlerShortRetryDelaySeconds { get; set; } = 5;
    public int ExcelShortRetryCount { get; set; } = 5;
    public int ExcelShortRetryDelaySeconds { get; set; } = 2;
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool RequireBackupBeforeExcelWrite { get; set; } = true;
    public bool CreateOutputWorksheetIfMissing { get; set; } = true;
    public bool ShowExcelSafetyPrompt { get; set; } = true;

    /// <summary>
    /// 持股列若「股名」儲存格套用這些 RGB 填滿色，就視為人工標記的不判斷資料。
    /// 預設 #92D050 為使用者提供範例中的綠色。
    /// </summary>
    public List<string> ExcludedHoldingFillColors { get; set; } = ["#92D050"];

    /// <summary>持股列出現以下文字時略過；用於「不判斷、已出場」等人工註記。</summary>
    public List<string> ExcludedHoldingTextMarkers { get; set; } = ["不判斷", "不用判斷", "忽略", "已出場", "暫停判斷"];

    public List<SourceDefinitionSetting> Sources { get; set; } = SourceDefinitionSetting.CreateDefaults();

    public static AppSettings CreateDefault() => new();
}

/// <summary>可設定 N 個網址；每一個 ProviderKey 對應一個獨立爬蟲實作。</summary>
public sealed class SourceDefinitionSetting
{
    public string SourceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public IndicatorType IndicatorType { get; set; }
    public string ProviderKey { get; set; } = "CnyesTechnicalAlignment";
    public bool Enabled { get; set; } = true;
    public bool Required { get; set; }

    public SourceDefinition ToDomain()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"來源網址格式不正確：{Url}");
        }

        return new SourceDefinition(SourceKey, DisplayName, uri, IndicatorType, ProviderKey, Enabled, Required);
    }

    public SourceDefinitionSetting Clone() => new()
    {
        SourceKey = SourceKey,
        DisplayName = DisplayName,
        Url = Url,
        IndicatorType = IndicatorType,
        ProviderKey = ProviderKey,
        Enabled = Enabled,
        Required = Required
    };

    public static List<SourceDefinitionSetting> CreateDefaults() =>
    [
        new()
        {
            SourceKey = "CNYES_BULLISH_ALIGNMENT",
            DisplayName = "鉅亨網－股價多頭排列",
            Url = "https://www.cnyes.com/twstock/a_technical4.aspx",
            IndicatorType = IndicatorType.BullishAlignment,
            ProviderKey = "CnyesTechnicalAlignment",
            Enabled = true,
            Required = true
        },
        new()
        {
            SourceKey = "CNYES_BEARISH_ALIGNMENT",
            DisplayName = "鉅亨網－股價空頭排列",
            Url = "https://www.cnyes.com/twstock/a_technical5.aspx",
            IndicatorType = IndicatorType.BearishAlignment,
            ProviderKey = "CnyesTechnicalAlignment",
            Enabled = true,
            Required = true
        }
    ];
}
