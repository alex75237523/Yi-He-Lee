using YiHeLee.Domain;

namespace YiHeLee.Application.Services;

/// <summary>集中管理 Excel 持股列的人工排除規則，避免顏色與文字判斷散落在 UI 或 Repository。</summary>
public sealed class HoldingRowExclusionService
{
    public bool ShouldExclude(AppSettings settings, string? stockNameFillColor, IEnumerable<string?> rowTexts)
    {
        if (SettingsValidationService.TryNormalizeColor(stockNameFillColor, out var normalizedFill))
        {
            var excludedColors = settings.ExcludedHoldingFillColors
                .Select(x => SettingsValidationService.TryNormalizeColor(x, out var normalized) ? normalized : string.Empty)
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (excludedColors.Contains(normalizedFill))
            {
                return true;
            }
        }

        var combinedText = string.Join(" ", rowTexts.Where(x => !string.IsNullOrWhiteSpace(x)));
        return settings.ExcludedHoldingTextMarkers.Any(marker =>
            !string.IsNullOrWhiteSpace(marker)
            && combinedText.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
