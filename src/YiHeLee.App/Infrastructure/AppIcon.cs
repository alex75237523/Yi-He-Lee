using System.Runtime.InteropServices;
using YiHeLee.Domain;

namespace YiHeLee.App.Infrastructure;

/// <summary>集中載入應用程式圖示；自訂路徑失效時一律退回內建圖示，避免外觀設定影響主流程。</summary>
internal static class AppIcon
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ico",
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp"
    };

    public static Icon Create(AppSettings? settings) => CreateFromPath(settings?.AppIconPath);

    public static Icon CreateDefault() => CreateFromPath(null);

    private static Icon CreateFromPath(string? configuredPath)
    {
        foreach (var path in CandidatePaths(configuredPath))
        {
            try
            {
                if (!File.Exists(path) || !SupportedImageExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
                {
                    return new Icon(path);
                }

                return CreateIconFromImage(path);
            }
            catch
            {
                // 圖示檔損毀或格式不支援時改用下一個候選，不讓程式因此無法啟動。
            }
        }

        try
        {
            var extracted = Icon.ExtractAssociatedIcon(BuildInfo.ExecutablePath);
            if (extracted is not null)
            {
                return extracted;
            }
        }
        catch
        {
            // 取 EXE 圖示失敗時使用系統預設圖示。
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static IEnumerable<string> CandidatePaths(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return ResolvePath(configuredPath);
        }

        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "YiHeLee.ico");
    }

    private static string ResolvePath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(AppContext.BaseDirectory, trimmed);
    }

    private static Icon CreateIconFromImage(string path)
    {
        using var source = Image.FromFile(path);
        var side = Math.Min(source.Width, source.Height);
        var sourceRect = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);

        using var bitmap = new Bitmap(256, 256);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, 256, 256), sourceRect, GraphicsUnit.Pixel);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
