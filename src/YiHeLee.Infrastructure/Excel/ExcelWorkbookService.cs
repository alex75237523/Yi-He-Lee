using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExcelInterop = Microsoft.Office.Interop.Excel;
using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Exceptions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Infrastructure.Excel;

public sealed partial class ExcelWorkbookService : IExcelWorkbookService
{
    private const int OutputColumnCount = 19;
    private readonly string _backupDirectory;
    private readonly IAppLogger _logger;
    private readonly HoldingRowExclusionService _holdingRowExclusionService;

    public ExcelWorkbookService(
        string backupDirectory,
        IAppLogger logger,
        HoldingRowExclusionService holdingRowExclusionService)
    {
        _backupDirectory = backupDirectory;
        _logger = logger;
        _holdingRowExclusionService = holdingRowExclusionService;
        Directory.CreateDirectory(_backupDirectory);
    }

    public Task<IReadOnlyList<CustomerHolding>> ReadHoldingsAsync(
        AppSettings settings,
        DateOnly targetDate,
        CancellationToken cancellationToken)
        => ExecuteWithExcelRetryAsync(
            settings,
            () => ReadHoldingsCore(settings, targetDate),
            "讀取 Excel 持股",
            cancellationToken);

    public Task WriteStrategyResultsAsync(
        AppSettings settings,
        DateOnly targetDate,
        IReadOnlyList<StrategyAlert> alerts,
        CancellationToken cancellationToken)
        => ExecuteWithExcelRetryAsync(
            settings,
            () =>
            {
                WriteStrategyResultsCore(settings, targetDate, alerts);
                return true;
            },
            "寫入 Excel 策略結果",
            cancellationToken);

    private IReadOnlyList<CustomerHolding> ReadHoldingsCore(AppSettings settings, DateOnly targetDate)
    {
        ExcelInterop.Workbook? workbook = null;
        ExcelInterop.Application? application = null;
        ExcelInterop.Sheets? worksheets = null;
        try
        {
            workbook = ExcelRunningObjectResolver.FindOpenWorkbook(settings.WorkbookPath);
            application = workbook.Application;
            EnsureExcelAvailable(application, workbook);
            worksheets = workbook.Worksheets;

            var excluded = settings.ExcludedWorksheetNames
                .Append(settings.OutputWorksheetName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var holdings = new List<CustomerHolding>();

            for (var sheetIndex = 1; sheetIndex <= worksheets.Count; sheetIndex++)
            {
                ExcelInterop.Worksheet? worksheet = null;
                try
                {
                    worksheet = (ExcelInterop.Worksheet)worksheets[sheetIndex];
                    if (excluded.Contains(worksheet.Name))
                    {
                        continue;
                    }

                    holdings.AddRange(ReadHoldingsFromWorksheet(workbook.FullName, worksheet, targetDate, settings));
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }

            return holdings
                .GroupBy(x => x.HoldingKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.SheetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ExcelRow)
                .ToArray();
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
            ComObject.FinalRelease(application);
            ComObject.FinalRelease(workbook);
        }
    }

    private IReadOnlyList<CustomerHolding> ReadHoldingsFromWorksheet(
        string workbookPath,
        ExcelInterop.Worksheet worksheet,
        DateOnly targetDate,
        AppSettings settings)
    {
        ExcelInterop.Range? usedRange = null;
        ExcelInterop.Range? usedRows = null;
        ExcelInterop.Range? usedColumns = null;
        try
        {
            usedRange = worksheet.UsedRange;
            usedRows = usedRange.Rows;
            usedColumns = usedRange.Columns;
            var rowCount = usedRows.Count;
            var totalColumnCount = usedColumns.Count;
            var columnCount = Math.Min(totalColumnCount, 80);
            if (rowCount < 2 || columnCount < 3)
            {
                return [];
            }

            var firstRow = usedRange.Row;
            var firstColumn = usedRange.Column;
            var values = usedRange.Value2;
            var accessor = new CellValueAccessor(values, rowCount, totalColumnCount);
            var customerName = FindCustomerName(accessor, rowCount, columnCount, worksheet.Name);
            var headers = FindActiveHoldingHeaders(accessor, rowCount, columnCount);
            var result = new List<CustomerHolding>();

            foreach (var header in headers)
            {
                for (var relativeRow = header.RelativeRow + 1; relativeRow <= header.EndRelativeRow; relativeRow++)
                {
                    var absoluteRow = firstRow + relativeRow - 1;
                    var absoluteCodeColumn = firstColumn + header.CodeColumn - 1;
                    var stockCode = ReadDisplayedCellText(worksheet, absoluteRow, absoluteCodeColumn);
                    stockCode = NormalizeStockCode(stockCode);
                    if (!StockCodeRegex().IsMatch(stockCode))
                    {
                        continue;
                    }

                    var stockName = accessor.GetString(relativeRow, header.NameColumn);
                    var absoluteNameColumn = firstColumn + header.NameColumn - 1;
                    var fillColor = ReadCellFillColorHex(worksheet, absoluteRow, absoluteNameColumn);
                    var rowTexts = Enumerable.Range(1, columnCount)
                        .Select(column => accessor.GetString(relativeRow, column));
                    if (_holdingRowExclusionService.ShouldExclude(settings, fillColor, rowTexts))
                    {
                        _logger.Info($"頁籤「{worksheet.Name}」第 {absoluteRow} 列股票 {stockCode} 因人工顏色／文字標記而略過判斷。");
                        continue;
                    }

                    if (!TryGetDecimal(accessor.GetValue(relativeRow, header.EntryPriceColumn), out var entryPrice)
                        || entryPrice <= 0)
                    {
                        _logger.Warning($"頁籤「{worksheet.Name}」第 {absoluteRow} 列股票 {stockCode} 的進場價／平均價無效，已略過。");
                        continue;
                    }

                    decimal? quantity = null;
                    if (header.QuantityColumn is not null
                        && TryGetDecimal(accessor.GetValue(relativeRow, header.QuantityColumn.Value), out var parsedQuantity))
                    {
                        quantity = parsedQuantity;
                    }

                    var holdingKey = string.Join("|",
                        Path.GetFullPath(workbookPath).ToUpperInvariant(),
                        worksheet.Name.ToUpperInvariant(),
                        absoluteRow.ToString(CultureInfo.InvariantCulture),
                        stockCode);

                    result.Add(new CustomerHolding(
                        targetDate,
                        Path.GetFullPath(workbookPath),
                        worksheet.Name,
                        customerName,
                        absoluteRow,
                        stockCode,
                        stockName,
                        entryPrice,
                        quantity,
                        holdingKey));
                }
            }

            return result;
        }
        finally
        {
            ComObject.FinalRelease(usedColumns);
            ComObject.FinalRelease(usedRows);
            ComObject.FinalRelease(usedRange);
        }
    }

    private void WriteStrategyResultsCore(AppSettings settings, DateOnly targetDate, IReadOnlyList<StrategyAlert> alerts)
    {
        ExcelInterop.Workbook? workbook = null;
        ExcelInterop.Application? application = null;
        ExcelInterop.Sheets? worksheets = null;
        ExcelInterop.Worksheet? worksheet = null;
        ExcelInterop.Range? clearRange = null;
        ExcelInterop.Range? dataRange = null;
        ExcelInterop.Range? titleRange = null;
        ExcelInterop.Range? headerRange = null;
        ExcelInterop.Range? verificationCell = null;
        try
        {
            workbook = ExcelRunningObjectResolver.FindOpenWorkbook(settings.WorkbookPath);
            application = workbook.Application;
            EnsureExcelAvailable(application, workbook);
            if (settings.RequireBackupBeforeExcelWrite)
            {
                CreateBackup(workbook, settings.WorkbookPath);
            }

            worksheets = workbook.Worksheets;
            worksheet = GetOrCreateOutputWorksheet(worksheets, settings);

            if (worksheet.ProtectContents)
            {
                throw new NonRetryableExcelJobException(
                    JobStatus.ExcelWriteFailed,
                    $"頁籤「{settings.OutputWorksheetName}」目前受保護，請先解除保護後再執行。");
            }

            var ordered = alerts
                .OrderBy(x => x.AlertKind == AlertKind.MovingAverageTriggered ? 0 : 1)
                .ThenBy(x => x.CustomerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.StockCode, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var lastClearRow = Math.Max(10_000, ordered.Length + 20);
            clearRange = worksheet.Range[$"A1:S{lastClearRow}"];
            clearRange.Clear();

            var rowCount = Math.Max(5, ordered.Length + 4);
            var matrix = new object[rowCount, OutputColumnCount];
            matrix[0, 0] = "Yi He Lee－每日五日均價策略";
            matrix[1, 0] = "資料日期";
            matrix[1, 1] = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            matrix[1, 2] = "策略通知筆數";
            matrix[1, 3] = ordered.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered);
            matrix[1, 4] = "未取得技術指標筆數";
            matrix[1, 5] = ordered.Count(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing);
            matrix[2, 0] = "判斷規則";
            matrix[2, 1] = "5日均價、20日均價或120日均價 <= 進場價/平均價（依 TWSE／TPEx 官方收盤價計算）；60日均價僅顯示。";

            var headers = new[]
            {
                "交易日期", "客戶頁籤", "客戶姓名", "原始列", "代碼", "股名", "進場價/平均價", "張數",
                "收盤價", "5日均價", "20日均價", "60日均價", "120日均價", "觸發條件", "市場", "排列類型", "來源網址",
                "資料來源", "計算時間"
            };
            for (var column = 0; column < headers.Length; column++)
            {
                matrix[3, column] = headers[column];
            }

            for (var index = 0; index < ordered.Length; index++)
            {
                var alert = ordered[index];
                var row = index + 4;
                matrix[row, 0] = alert.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                matrix[row, 1] = alert.SheetName;
                matrix[row, 2] = alert.CustomerName;
                matrix[row, 3] = alert.ExcelRow;
                matrix[row, 4] = alert.StockCode;
                matrix[row, 5] = alert.StockName;
                matrix[row, 6] = alert.EntryAveragePrice;
                matrix[row, 7] = alert.Quantity;
                matrix[row, 8] = alert.ClosePrice;
                matrix[row, 9] = alert.MovingAverage5;
                matrix[row, 10] = alert.MovingAverage20;
                matrix[row, 11] = alert.MovingAverage60;
                matrix[row, 12] = alert.MovingAverage120;
                matrix[row, 13] = alert.TriggerDescription;
                matrix[row, 14] = ToMarketText(alert.MarketType);
                matrix[row, 15] = ToIndicatorText(alert.IndicatorType);
                matrix[row, 16] = alert.SourceUrl;
                matrix[row, 17] = alert.PriceSourceProvider ?? string.Empty;
                matrix[row, 18] = alert.CalculatedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
            }

            dataRange = worksheet.Range[$"A1:S{rowCount}"];
            dataRange.Value2 = matrix;
            titleRange = worksheet.Range["A1:S1"];
            titleRange.Merge();
            titleRange.Font.Bold = true;
            titleRange.Font.Size = 15;
            titleRange.HorizontalAlignment = ExcelInterop.XlHAlign.xlHAlignCenter;
            titleRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightBlue);

            headerRange = worksheet.Range["A4:S4"];
            headerRange.Font.Bold = true;
            headerRange.HorizontalAlignment = ExcelInterop.XlHAlign.xlHAlignCenter;
            headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.FromArgb(189, 215, 238));
            headerRange.Borders.LineStyle = ExcelInterop.XlLineStyle.xlContinuous;

            FormatOutputColumns(worksheet, rowCount, ordered);

            // 寫入後先驗證關鍵統計，再儲存整份活頁簿。
            verificationCell = (ExcelInterop.Range)worksheet.Cells[2, 4];
            var writtenAlertCount = Convert.ToInt32(verificationCell.Value2 ?? 0, CultureInfo.InvariantCulture);
            var expectedAlertCount = ordered.Count(x => x.AlertKind == AlertKind.MovingAverageTriggered);
            if (writtenAlertCount != expectedAlertCount)
            {
                throw new InvalidOperationException($"Excel 寫入驗證失敗：預期通知 {expectedAlertCount} 筆，實際 {writtenAlertCount} 筆。");
            }

            workbook.Save();
        }
        finally
        {
            ComObject.FinalRelease(verificationCell);
            ComObject.FinalRelease(headerRange);
            ComObject.FinalRelease(titleRange);
            ComObject.FinalRelease(dataRange);
            ComObject.FinalRelease(clearRange);
            ComObject.FinalRelease(worksheet);
            ComObject.FinalRelease(worksheets);
            ComObject.FinalRelease(application);
            ComObject.FinalRelease(workbook);
        }
    }

    private static void FormatOutputColumns(ExcelInterop.Worksheet worksheet, int rowCount, IReadOnlyList<StrategyAlert> alerts)
    {
        ExcelInterop.Range? allRange = null;
        ExcelInterop.Range? codeColumn = null;
        ExcelInterop.Range? numericRange = null;
        ExcelInterop.Range? missingRange = null;
        try
        {
            allRange = worksheet.Range[$"A4:S{rowCount}"];
            allRange.Borders.LineStyle = ExcelInterop.XlLineStyle.xlContinuous;
            allRange.VerticalAlignment = ExcelInterop.XlVAlign.xlVAlignCenter;
            allRange.WrapText = true;

            codeColumn = worksheet.Range[$"E5:E{rowCount}"];
            codeColumn.NumberFormat = "@";
            numericRange = worksheet.Range[$"G5:M{rowCount}"];
            numericRange.NumberFormat = "0.00";

            SetColumnWidth(worksheet, "A", 12);
            SetColumnWidth(worksheet, "B", 18);
            SetColumnWidth(worksheet, "C", 14);
            SetColumnWidth(worksheet, "D", 9);
            SetColumnWidth(worksheet, "E", 12);
            SetColumnWidth(worksheet, "F", 18);
            SetColumnWidth(worksheet, "G", 15);
            SetColumnWidth(worksheet, "H", 9);
            SetColumnWidth(worksheet, "I", 11);
            SetColumnWidth(worksheet, "J", 11);
            SetColumnWidth(worksheet, "K", 11);
            SetColumnWidth(worksheet, "L", 11);
            SetColumnWidth(worksheet, "M", 12);
            SetColumnWidth(worksheet, "N", 42);
            SetColumnWidth(worksheet, "O", 12);
            SetColumnWidth(worksheet, "P", 14);
            SetColumnWidth(worksheet, "Q", 48);
            SetColumnWidth(worksheet, "R", 12);
            SetColumnWidth(worksheet, "S", 20);

            var firstMissingIndex = alerts.ToList().FindIndex(x => x.AlertKind == AlertKind.TechnicalIndicatorMissing);
            if (firstMissingIndex >= 0)
            {
                var startRow = firstMissingIndex + 5;
                var endRow = alerts.Count + 4;
                missingRange = worksheet.Range[$"A{startRow}:S{endRow}"];
                missingRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);
            }
        }
        finally
        {
            ComObject.FinalRelease(missingRange);
            ComObject.FinalRelease(numericRange);
            ComObject.FinalRelease(codeColumn);
            ComObject.FinalRelease(allRange);
        }
    }

    private static void SetColumnWidth(ExcelInterop.Worksheet worksheet, string column, double width)
    {
        ExcelInterop.Range? range = null;
        try
        {
            range = worksheet.Range[$"{column}:{column}"];
            range.ColumnWidth = width;
        }
        finally
        {
            ComObject.FinalRelease(range);
        }
    }

    private static ExcelInterop.Worksheet GetOrCreateOutputWorksheet(ExcelInterop.Sheets worksheets, AppSettings settings)
    {
        for (var index = 1; index <= worksheets.Count; index++)
        {
            ExcelInterop.Worksheet? item = null;
            var isTarget = false;
            try
            {
                item = (ExcelInterop.Worksheet)worksheets[index];
                isTarget = string.Equals(item.Name, settings.OutputWorksheetName, StringComparison.OrdinalIgnoreCase);
                if (isTarget)
                {
                    return item;
                }
            }
            finally
            {
                if (!isTarget)
                {
                    ComObject.FinalRelease(item);
                }
            }
        }

        if (!settings.CreateOutputWorksheetIfMissing)
        {
            throw new NonRetryableExcelJobException(
                JobStatus.ExcelWriteFailed,
                $"找不到輸出頁籤「{settings.OutputWorksheetName}」，且設定不允許自動建立。");
        }

        ExcelInterop.Worksheet? lastSheet = null;
        try
        {
            lastSheet = (ExcelInterop.Worksheet)worksheets[worksheets.Count];
            var created = (ExcelInterop.Worksheet)worksheets.Add(After: lastSheet);
            created.Name = settings.OutputWorksheetName;
            return created;
        }
        finally
        {
            ComObject.FinalRelease(lastSheet);
        }
    }

    private void CreateBackup(ExcelInterop.Workbook workbook, string workbookPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(workbookPath);
            var backupName = $"{Path.GetFileNameWithoutExtension(fullPath)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}{Path.GetExtension(fullPath)}";
            var backupPath = Path.Combine(_backupDirectory, backupName);

            // 使用 Excel SaveCopyAs 保存目前記憶體中的狀態，包含使用者尚未手動儲存的內容。
            workbook.SaveCopyAs(backupPath);
        }
        catch (COMException)
        {
            // 交由外層判斷是否為 Excel 忙碌並執行短暫重試。
            throw;
        }
        catch (Exception ex)
        {
            throw new NonRetryableExcelJobException(
                JobStatus.ExcelWriteFailed,
                "寫入 Excel 前建立備份失敗，已停止寫入以保護原始檔案。請確認備份資料夾權限與磁碟空間。",
                ex);
        }
    }

    private static void EnsureExcelAvailable(ExcelInterop.Application application, ExcelInterop.Workbook workbook)
    {
        if (!application.Ready)
        {
            throw new ExcelBusyException("Excel 目前忙碌，可能正在編輯儲存格、開啟對話框、計算公式或執行巨集。");
        }

        if (workbook.ReadOnly)
        {
            throw new NonRetryableExcelJobException(
                JobStatus.ExcelUnavailable,
                "指定 Excel 活頁簿目前為唯讀或受保護檢視。請先啟用編輯並確認檔案可寫入，再重新執行。");
        }
    }

    private async Task<T> ExecuteWithExcelRetryAsync<T>(
        AppSettings settings,
        Func<T> action,
        string operationName,
        CancellationToken cancellationToken)
    {
        return await StaTaskRunner.RunAsync(() =>
        {
            Exception? lastError = null;
            var maxAttempts = Math.Max(1, settings.ExcelShortRetryCount);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return action();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (NonRetryableExcelJobException)
                {
                    throw;
                }
                catch (ExcelBusyException ex) when (attempt < maxAttempts)
                {
                    lastError = ex;
                }
                catch (COMException ex) when (IsExcelBusy(ex) && attempt < maxAttempts)
                {
                    lastError = ex;
                }
                catch (COMException ex) when (IsExcelBusy(ex))
                {
                    lastError = ex;
                    break;
                }
                catch (Exception ex)
                {
                    throw new RetryableExcelJobException(
                        GetExcelFailureStatus(operationName),
                        $"{operationName}失敗：{ex.Message}",
                        ex);
                }

                _logger.Warning($"{operationName}第 {attempt} 次遇到 Excel 忙碌，等待後重試。原因：{lastError?.Message}");
                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, settings.ExcelShortRetryDelaySeconds)));
            }

            throw new RetryableExcelJobException(
                GetExcelFailureStatus(operationName),
                $"{operationName}連續 {Math.Max(1, settings.ExcelShortRetryCount)} 次失敗。請先按 Enter 或 Esc、關閉 Excel 對話框，並保持活頁簿開啟。",
                lastError);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static JobStatus GetExcelFailureStatus(string operationName)
        => operationName.StartsWith("讀取", StringComparison.Ordinal)
            ? JobStatus.ExcelUnavailable
            : JobStatus.ExcelWriteFailed;

    private static bool IsExcelBusy(COMException exception)
    {
        var hresult = exception.HResult;
        return hresult == unchecked((int)0x80010001)
               || hresult == unchecked((int)0x8001010A)
               || hresult == unchecked((int)0x800AC472);
    }

    private static IReadOnlyList<HeaderDefinition> FindActiveHoldingHeaders(
        CellValueAccessor accessor,
        int rowCount,
        int columnCount)
    {
        var candidates = new List<TableHeaderCandidate>();
        for (var row = 1; row <= rowCount; row++)
        {
            int? code = null;
            int? name = null;
            int? entry = null;
            int? quantity = null;
            var containsExitPrice = false;

            for (var column = 1; column <= columnCount; column++)
            {
                var text = NormalizeHeader(accessor.GetString(row, column));
                if (text is "代號" or "代碼" or "股票代碼") code = column;
                if (text is "股名" or "名稱" or "股票名稱") name = column;
                if (text.Contains("進場價", StringComparison.Ordinal)
                    && text.Contains("平均價", StringComparison.Ordinal)) entry = column;
                if (text == "張數") quantity = column;
                if (text.Contains("出場價", StringComparison.Ordinal)) containsExitPrice = true;
            }

            // 已出場表頭也要記錄為區塊邊界，避免上一個持股區塊一路掃描到已出場資料。
            if (code is not null && name is not null && (entry is not null || containsExitPrice))
            {
                candidates.Add(new TableHeaderCandidate(
                    row,
                    code.Value,
                    name.Value,
                    entry,
                    quantity,
                    containsExitPrice));
            }
        }

        var result = new List<HeaderDefinition>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.ContainsExitPrice || candidate.EntryPriceColumn is null)
            {
                continue;
            }

            var endRelativeRow = index + 1 < candidates.Count
                ? candidates[index + 1].RelativeRow - 1
                : rowCount;
            result.Add(new HeaderDefinition(
                candidate.RelativeRow,
                endRelativeRow,
                candidate.CodeColumn,
                candidate.NameColumn,
                candidate.EntryPriceColumn.Value,
                candidate.QuantityColumn));
        }

        return result;
    }

    private static string FindCustomerName(
        CellValueAccessor accessor,
        int rowCount,
        int columnCount,
        string fallback)
    {
        var maxRows = Math.Min(15, rowCount);
        var maxColumns = Math.Min(15, columnCount);
        for (var row = 1; row <= maxRows; row++)
        {
            for (var column = 1; column <= maxColumns; column++)
            {
                if (NormalizeHeader(accessor.GetString(row, column)) != "姓名")
                {
                    continue;
                }

                var below = row < rowCount ? accessor.GetString(row + 1, column) : string.Empty;
                if (!string.IsNullOrWhiteSpace(below))
                {
                    return below.Trim();
                }

                var right = column < columnCount ? accessor.GetString(row, column + 1) : string.Empty;
                if (!string.IsNullOrWhiteSpace(right))
                {
                    return right.Trim();
                }
            }
        }

        return fallback;
    }

    private static string ReadCellFillColorHex(ExcelInterop.Worksheet worksheet, int row, int column)
    {
        ExcelInterop.Range? cell = null;
        ExcelInterop.Interior? interior = null;
        try
        {
            cell = (ExcelInterop.Range)worksheet.Cells[row, column];
            interior = cell.Interior;
            var colorIndex = Convert.ToInt32(interior.ColorIndex, CultureInfo.InvariantCulture);
            if (colorIndex == (int)ExcelInterop.XlColorIndex.xlColorIndexNone)
            {
                return string.Empty;
            }

            var oleColor = Convert.ToInt32(interior.Color, CultureInfo.InvariantCulture);
            var color = System.Drawing.ColorTranslator.FromOle(oleColor);
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch
        {
            // 顏色讀取失敗時不可誤排除正常持股；回傳空值繼續依文字規則判斷。
            return string.Empty;
        }
        finally
        {
            ComObject.FinalRelease(interior);
            ComObject.FinalRelease(cell);
        }
    }

    private static string ReadDisplayedCellText(ExcelInterop.Worksheet worksheet, int row, int column)
    {
        ExcelInterop.Range? cell = null;
        try
        {
            cell = (ExcelInterop.Range)worksheet.Cells[row, column];
            return Convert.ToString(cell.Text, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
        }
        finally
        {
            ComObject.FinalRelease(cell);
        }
    }

    private static bool TryGetDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case decimal decimalValue:
                result = decimalValue;
                return true;
            case double doubleValue:
                result = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
                return true;
            case float floatValue:
                result = Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            default:
                var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
                return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result)
                       || decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.GetCultureInfo("zh-TW"), out result);
        }
    }

    private static string NormalizeHeader(string value)
        => Regex.Replace(value ?? string.Empty, @"\s+", string.Empty)
            .Replace("／", "/", StringComparison.Ordinal)
            .Trim();

    private static string NormalizeStockCode(string value)
        => Regex.Replace(value ?? string.Empty, @"\s+", string.Empty).ToUpperInvariant();

    private static string ToMarketText(MarketType? marketType) => marketType switch
    {
        MarketType.Listed => "集中市場",
        MarketType.Otc => "店頭市場",
        _ => string.Empty
    };

    private static string ToIndicatorText(IndicatorType? indicatorType) => indicatorType switch
    {
        IndicatorType.BullishAlignment => "股價多頭排列",
        IndicatorType.BearishAlignment => "股價空頭排列",
        _ => string.Empty
    };

    [GeneratedRegex(@"^[0-9A-Z]{4,10}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StockCodeRegex();

    private sealed record TableHeaderCandidate(
        int RelativeRow,
        int CodeColumn,
        int NameColumn,
        int? EntryPriceColumn,
        int? QuantityColumn,
        bool ContainsExitPrice);

    private sealed record HeaderDefinition(
        int RelativeRow,
        int EndRelativeRow,
        int CodeColumn,
        int NameColumn,
        int EntryPriceColumn,
        int? QuantityColumn);

    private sealed class CellValueAccessor
    {
        private readonly object? _rawValue;
        private readonly object[,]? _values;
        private readonly int _rowCount;
        private readonly int _columnCount;

        public CellValueAccessor(object? value, int rowCount, int columnCount)
        {
            _rawValue = value;
            _values = value as object[,];
            _rowCount = rowCount;
            _columnCount = columnCount;
        }

        public object? GetValue(int row, int column)
        {
            if (row < 1 || column < 1 || row > _rowCount || column > _columnCount)
            {
                return null;
            }

            if (_values is not null)
            {
                return _values[row, column];
            }

            return row == 1 && column == 1 ? _rawValue : null;
        }

        public string GetString(int row, int column)
            => Convert.ToString(GetValue(row, column), CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
    }

    private sealed class ExcelBusyException : Exception
    {
        public ExcelBusyException(string message) : base(message) { }
    }
}
