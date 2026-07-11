using System.Text.Json;

namespace YiHeLee.App.Infrastructure;

/// <summary>
/// 讀取實際執行版本資訊，供啟動 Log 與主畫面顯示，方便確認目前啟動的不是舊資料夾裡的舊 EXE。
/// 正式發佈由 <c>scripts/publish-win-x64.ps1</c> 產生 <c>buildinfo.json</c>（含 Git Commit SHA、
/// 分支、Build 時間），與發佈輸出放在同一個資料夾；直接以 <c>dotnet build</c>／<c>dotnet run</c>
/// 執行、沒有 <c>buildinfo.json</c> 時，退回以組件版本與 EXE 檔案時間顯示，並明確標示「非正式 Publish」。
/// </summary>
internal static class BuildInfo
{
    public static string Version { get; }
    public static string GitCommitSha { get; }
    public static string GitBranch { get; }
    public static string BuildTimeUtc { get; }
    public static string ExecutablePath { get; }
    public static bool IsFromPublishScript { get; }

    static BuildInfo()
    {
        ExecutablePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Yi He Lee.exe");

        var assembly = typeof(BuildInfo).Assembly;
        Version = assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        var buildInfoPath = Path.Combine(AppContext.BaseDirectory, "buildinfo.json");
        if (File.Exists(buildInfoPath))
        {
            try
            {
                using var stream = File.OpenRead(buildInfoPath);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                GitCommitSha = ReadStringOrDefault(root, "GitCommitSha", "unknown");
                GitBranch = ReadStringOrDefault(root, "GitBranch", "unknown");
                BuildTimeUtc = ReadStringOrDefault(root, "BuildTimeUtc", "unknown");
                var versionFromFile = ReadStringOrDefault(root, "Version", string.Empty);
                if (!string.IsNullOrWhiteSpace(versionFromFile))
                {
                    Version = versionFromFile;
                }

                IsFromPublishScript = true;
                return;
            }
            catch
            {
                // buildinfo.json 損毀或格式不符時不得讓程式因此無法啟動，改用保底資訊。
            }
        }

        IsFromPublishScript = false;
        GitCommitSha = "unknown（未透過 scripts/publish-win-x64.ps1 發佈，可能為開發環境直接 build／run）";
        GitBranch = "unknown";
        try
        {
            BuildTimeUtc = File.GetLastWriteTimeUtc(assembly.Location).ToString("O");
        }
        catch
        {
            BuildTimeUtc = "unknown";
        }
    }

    /// <summary>供主畫面標題列顯示的精簡版本字串，例如 "v1.0.0.0 (a1b2c3d4)"。</summary>
    public static string ShortDescription => $"v{Version} ({ShortCommitSha})";

    private static string ShortCommitSha
        => IsFromPublishScript && GitCommitSha.Length > 12 ? GitCommitSha[..12] : GitCommitSha;

    private static string ReadStringOrDefault(JsonElement root, string propertyName, string defaultValue)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;
}
