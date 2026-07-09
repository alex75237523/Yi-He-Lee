namespace YiHeLee.App;

/// <summary>
/// 可攜式資料路徑：一律以程式所在目錄（<see cref="AppContext.BaseDirectory"/>）為根目錄，
/// 讓整個發佈資料夾（含 settings.json、Data、Logs、Backups）可直接複製到其他可寫路徑使用。
/// 禁止改回 %LOCALAPPDATA% 等使用者設定檔路徑。
/// </summary>
internal sealed class AppPaths
{
    public AppPaths()
    {
        RootDirectory = AppContext.BaseDirectory;
        DataDirectory = Path.Combine(RootDirectory, "Data");
        LogDirectory = Path.Combine(RootDirectory, "Logs");
        BackupDirectory = Path.Combine(RootDirectory, "Backups");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
        DatabasePath = Path.Combine(DataDirectory, "yi-he-lee.db");

        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(BackupDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"無法在程式目錄「{RootDirectory}」建立 Data／Logs／Backups 資料夾，請確認此路徑可寫入。" +
                "可攜式模式不建議將程式放在「C:\\Program Files」等需要系統管理員權限的目錄，建議改放桌面、文件或其他可寫資料夾。",
                ex);
        }
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string BackupDirectory { get; }
    public string SettingsPath { get; }
    public string DatabasePath { get; }
}
