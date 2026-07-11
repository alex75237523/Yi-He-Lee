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
    private const int OutputColumnCount = 7;
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
        CancellationToken cancellationToken,
        Action<string>? reportProgress = null)
        => ExecuteWithExcelRetryAsync(
            settings,
            () => ReadHoldingsCore(settings, targetDate, reportProgress),
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

    private IReadOnlyList<CustomerHolding> ReadHoldingsCore(AppSettings settings, DateOnly targetDate, Action<string>? reportProgress)
    {
        ExcelInterop.Workbook? workbook = null;
        ExcelInterop.Application? application = null;
        ExcelInterop.Sheets? worksheets = null;
        try
        {
            workbook = ExcelRunningObjectResolver.FindOpenWorkbook(settings.WorkbookPath, settings.AutoOpenWorkbookIfClosed);
            application = workbook.Application;
            EnsureExcelAvailable(application, workbook);
            worksheets = workbook.Worksheets;

            var excluded = settings.ExcludedWorksheetNames
                .Append(settings.OutputWorksheetName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var holdings = new List<CustomerHolding>();

            var totalSheets = worksheets.Count;
            for (var sheetIndex = 1; sheetIndex <= totalSheets; sheetIndex++)
            {
                ExcelInterop.Worksheet? worksheet = null;
                try
                {
                    worksheet = (ExcelInterop.Worksheet)worksheets[sheetIndex];
                    if (excluded.Contains(worksheet.Name))
                    {
                        reportProgress?.Invoke($"正在讀取 Excel 客戶頁籤：略過「{worksheet.Name}」（{sheetIndex}/{totalSheets}）。");
                        continue;
                    }

                    reportProgress?.Invoke($"正在讀取 Excel 客戶頁籤：「{worksheet.Name}」（{sheetIndex}/{totalSheets}）……");
                    var sheetHoldings = ReadHoldingsFromWorksheet(workbook.FullName, worksheet, targetDate, settings);
                    holdings.AddRange(sheetHoldings);
                    var customerName = sheetHoldings.FirstOrDefault()?.CustomerName ?? worksheet.Name;
                    reportProgress?.Invoke($"已讀取客戶頁籤：「{worksheet.Name}」／客戶「{customerName}」，找到 {sheetHoldings.Count} 筆持股（{sheetIndex}/{totalSheets}）。");
                }
                finally
                {
                    ComObject.FinalRelease(worksheet);
                }
            }

            var distinctHoldings = holdings
                .GroupBy(x => x.HoldingKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.SheetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ExcelRow)
                .ToArray();
            reportProgress?.Invoke($"Excel 客戶頁籤讀取完成：共 {distinctHoldings.Length} 筆有效持股。");
            return distinctHoldings;
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

                    // 「現價」欄位串接外部 DDE，不能假設一定是正常數字；無法判讀時不得靜默略過，
                    // 必須帶著原因回傳，由策略層轉成「現價異常」通知告知使用者。
                    var currentPrice = CurrentPriceCellParser.Parse(accessor.GetValue(relativeRow, header.CurrentPriceColumn));
                    if (!currentPrice.IsValid)
                    {
                        _logger.Warning($"頁籤「{worksheet.Name}」第 {absoluteRow} 列股票 {stockCode} 的現價無法判讀：{currentPrice.Issue}。");
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
                        currentPrice.Price,
                        quantity,
                        holdingKey,
                        currentPrice.Issue));
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
        ExcelInterop.Range? headerRange = null;
        try
        {
            workbook = ExcelRunningObjectResolver.FindOpenWorkbook(settings.WorkbookPath, settings.AutoOpenWorkbookIfClosed);
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
                .OrderBy(x => x.AlertKind switch
                {
                    AlertKind.MovingAverageTriggered => 0,
                    AlertKind.CurrentPriceInvalid => 1,
                    _ => 2
                })
                .ThenBy(x => x.CustomerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.StockCode, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var lastClearRow = Math.Max(10_000, ordered.Length + 20);
            clearRange = worksheet.Range[$"A1:G{lastClearRow}"];
            // 先解除範圍內殘留的合併儲存格，否則邊界跨出 A:G 的合併儲存格會讓 Clear() 拋出
            // COMException 0x800A03EC（無法對合併儲存格執行該動作）。MergeCells 在範圍內合併狀態
            // 不一致時會回傳 DBNull，因此不判斷該屬性，直接呼叫 UnMerge()（未合併時為無動作）。
            clearRange.UnMerge();
            clearRange.Clear();

            var rowCount = Math.Max(2, ordered.Length + 1);
            var matrix = new object[rowCount, OutputColumnCount];

            var headers = new[]
            {
                "代碼", "名稱", "收盤價", "5日均價", "20日均價", "60日均價", "120日均價"
            };
            for (var column = 0; column < headers.Length; column++)
            {
                matrix[0, column] = headers[column];
            }

            for (var index = 0; index < ordered.Length; index++)
            {
                var alert = ordered[index];
                var row = index + 1;
                matrix[row, 0] = alert.StockCode;
                matrix[row, 1] = alert.StockName;
                matrix[row, 2] = alert.ClosePrice;
                matrix[row, 3] = alert.MovingAverage5;
                matrix[row, 4] = alert.MovingAverage20;
                matrix[row, 5] = alert.MovingAverage60;
                matrix[row, 6] = alert.MovingAverage120;
            }

            dataRange = worksheet.Range[$"A1:G{rowCount}"];
            dataRange.Value2 = matrix;

            headerRange = worksheet.Range["A1:G1"];
            headerRange.Font.Bold = true;
            headerRange.HorizontalAlignment = ExcelInterop.XlHAlign.xlHAlignCenter;
            headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.FromArgb(189, 215, 238));
            headerRange.Borders.LineStyle = ExcelInterop.XlLineStyle.xlContinuous;

            FormatOutputColumns(worksheet, rowCount, ordered);

            workbook.Save();
        }
        finally
        {
            ComObject.FinalRelease(headerRange);
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
            allRange = worksheet.Range[$"A1:G{rowCount}"];
            allRange.Borders.LineStyle = ExcelInterop.XlLineStyle.xlContinuous;
            allRange.VerticalAlignment = ExcelInterop.XlVAlign.xlVAlignCenter;
            allRange.WrapText = true;

            codeColumn = worksheet.Range[$"A2:A{rowCount}"];
            codeColumn.NumberFormat = "@";
            numericRange = worksheet.Range[$"C2:G{rowCount}"];
            numericRange.NumberFormat = "0.00";

            SetColumnWidth(worksheet, "A", 12);
            SetColumnWidth(worksheet, "B", 18);
            SetColumnWidth(worksheet, "C", 11);
            SetColumnWidth(worksheet, "D", 11);
            SetColumnWidth(worksheet, "E", 11);
            SetColumnWidth(worksheet, "F", 11);
            SetColumnWidth(worksheet, "G", 12);

            // 現價異常與無技術資料的列一律以底色標示，提醒使用者這些持股本次未完成判斷。
            var firstMissingIndex = alerts.ToList().FindIndex(x => x.AlertKind != AlertKind.MovingAverageTriggered);
            if (firstMissingIndex >= 0)
            {
                var startRow = firstMissingIndex + 2;
                var endRow = alerts.Count + 1;
                missingRange = worksheet.Range[$"A{startRow}:G{endRow}"];
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
                catch (ExcelWorkbookResolutionException ex) when (!ex.IsRetryable)
                {
                    _logger.Warning(ex.DiagnosticMessage);
                    throw new NonRetryableExcelJobException(
                        JobStatus.ExcelUnavailable,
                        ex.Message,
                        ex);
                }
                catch (ExcelWorkbookResolutionException ex) when (attempt < maxAttempts)
                {
                    lastError = ex;
                    _logger.Warning(ex.DiagnosticMessage);
                }
                catch (ExcelWorkbookResolutionException ex)
                {
                    lastError = ex;
                    _logger.Warning(ex.DiagnosticMessage);
                    break;
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

                _logger.Warning($"{operationName}第 {attempt} 次暫時無法使用 Excel，等待後重試。原因：{lastError?.Message}");
                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, settings.ExcelShortRetryDelaySeconds)));
            }

            if (lastError is ExcelWorkbookResolutionException resolutionException)
            {
                throw new RetryableExcelJobException(
                    GetExcelFailureStatus(operationName),
                    $"{operationName}連續 {maxAttempts} 次無法連接活頁簿：{resolutionException.Message}",
                    resolutionException);
            }

            throw new RetryableExcelJobException(
                GetExcelFailureStatus(operationName),
                $"{operationName}連續 {maxAttempts} 次失敗。請先按 Enter 或 Esc、關閉 Excel 對話框，並保持活頁簿開啟。",
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
            int? current = null;
            int? quantity = null;
            var containsExitPrice = false;

            for (var column = 1; column <= columnCount; column++)
            {
                var text = NormalizeHeader(accessor.GetString(row, column));
                if (text is "代號" or "代碼" or "股票代碼") code = column;
                if (text is "股名" or "名稱" or "股票名稱") name = column;
                if (text == "現價") current = column;
                if (text == "張數") quantity = column;
                if (text.Contains("出場價", StringComparison.Ordinal)) containsExitPrice = true;
            }

            // 已出場表頭也要記錄為區塊邊界，避免上一個持股區塊一路掃描到已出場資料。
            if (code is not null && name is not null && (current is not null || containsExitPrice))
            {
                candidates.Add(new TableHeaderCandidate(
                    row,
                    code.Value,
                    name.Value,
                    current,
                    quantity,
                    containsExitPrice));
            }
        }

        var result = new List<HeaderDefinition>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.ContainsExitPrice || candidate.CurrentPriceColumn is null)
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
                candidate.CurrentPriceColumn.Value,
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

    [GeneratedRegex(@"^[0-9A-Z]{4,10}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StockCodeRegex();

    private sealed record TableHeaderCandidate(
        int RelativeRow,
        int CodeColumn,
        int NameColumn,
        int? CurrentPriceColumn,
        int? QuantityColumn,
        bool ContainsExitPrice);

    private sealed record HeaderDefinition(
        int RelativeRow,
        int EndRelativeRow,
        int CodeColumn,
        int NameColumn,
        int CurrentPriceColumn,
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
