using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ExcelInterop = Microsoft.Office.Interop.Excel;

namespace YiHeLee.Infrastructure.Excel;

/// <summary>
/// 連接使用者已開啟的 Excel 活頁簿。除 ROT／GetActiveObject 外，也會巡覽所有 Excel 視窗，
/// 避免多執行個體、活頁簿尚未註冊到 ROT 或 Excel 版本差異造成誤判。
/// </summary>
internal static class ExcelRunningObjectResolver
{
    private const uint ObjIdNativeOm = 0xFFFFFFF0;
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    public static ExcelInterop.Workbook FindOpenWorkbook(string workbookPath, bool autoOpenIfClosed = false)
    {
        string fullPath;
        try
        {
            fullPath = ExcelWorkbookPathMatcher.NormalizeConfiguredPath(workbookPath);
        }
        catch (Exception ex)
        {
            throw new ExcelWorkbookResolutionException(
                ex.Message,
                isRetryable: false,
                diagnosticMessage: ex.ToString(),
                innerException: ex);
        }

        if (!File.Exists(fullPath))
        {
            throw new ExcelWorkbookResolutionException(
                $"設定的 Excel 檔案不存在：{fullPath}。請重新選擇正確檔案。",
                isRetryable: false,
                diagnosticMessage: $"Excel 設定路徑不存在：{fullPath}");
        }

        var context = new ResolutionContext(fullPath);
        ExcelInterop.Workbook? workbook = null;

        try
        {
            workbook = TryFindFromRunningObjectTable(context);
            if (workbook is not null)
            {
                return workbook;
            }

            workbook = TryFindFromActiveExcel(context);
            if (workbook is not null)
            {
                return workbook;
            }

            workbook = TryFindFromExcelWindows(context);
            if (workbook is not null)
            {
                return workbook;
            }
        }
        catch (ExcelWorkbookResolutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.AddProbeError("解析 Excel 執行個體時發生未預期錯誤", ex);
        }

        var diagnostic = context.BuildDiagnosticMessage();

        if (context.ProtectedViewMatch)
        {
            throw new ExcelWorkbookResolutionException(
                "指定活頁簿目前位於 Excel『受保護的檢視』。請先在 Excel 黃色提示列按『啟用編輯』，確認檔案可編輯後再立即重試。",
                isRetryable: false,
                diagnosticMessage: diagnostic);
        }

        var sameNameCandidates = context.OpenWorkbookPaths
            .Where(path => ExcelWorkbookPathMatcher.HasSameFileName(path, fullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sameNameCandidates.Length == 1)
        {
            throw new ExcelWorkbookResolutionException(
                "Excel 已開啟同名活頁簿，但實際路徑與設定不一致。請到設定重新選取目前開啟的檔案，或關閉錯誤副本後再執行。" +
                $"\r\n設定路徑：{fullPath}\r\nExcel 實際路徑：{sameNameCandidates[0]}",
                isRetryable: false,
                diagnosticMessage: diagnostic);
        }

        if (autoOpenIfClosed)
        {
            var opened = TryAutoOpenWorkbook(fullPath, context);
            if (opened is not null)
            {
                return opened;
            }
        }

        var permissionHint = context.ExcelTopLevelWindowCount > 0 && context.NativeObjectSuccessCount == 0
            ? "已偵測到 Excel 視窗，但無法取得 Excel 自動化介面；最常見原因是 Excel 與 Yi He Lee 的權限層級不同。請兩者都用一般權限開啟，或兩者都用系統管理員權限開啟。"
            : "請確認指定檔案已用桌面版 Excel 開啟，並讓 Excel 與 Yi He Lee 使用相同權限層級。";

        throw new ExcelWorkbookResolutionException(
            $"找不到已開啟的指定 Excel 活頁簿。{permissionHint}",
            isRetryable: true,
            diagnosticMessage: diagnostic);
    }

    private static ExcelInterop.Workbook? TryFindFromRunningObjectTable(ResolutionContext context)
    {
        IRunningObjectTable? runningObjectTable = null;
        IEnumMoniker? enumMoniker = null;
        IBindCtx? bindContext = null;
        try
        {
            Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out runningObjectTable));
            runningObjectTable.EnumRunning(out enumMoniker);
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out bindContext));
            enumMoniker.Reset();

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                object? runningObject = null;
                try
                {
                    context.RotMonikerCount++;
                    string? displayName = null;
                    try
                    {
                        monikers[0].GetDisplayName(bindContext, null, out displayName);
                    }
                    catch (Exception ex)
                    {
                        context.AddProbeError("讀取 ROT Moniker 名稱失敗", ex);
                    }

                    // 不再先用 DisplayName 過濾。部分 Excel 版本／多執行個體的 Moniker 名稱不含完整路徑，
                    // 先過濾會把真正的 Workbook 當成無關物件略過。
                    runningObjectTable.GetObject(monikers[0], out runningObject);
                    context.RotObjectCount++;

                    if (runningObject is ExcelInterop.Workbook workbook)
                    {
                        context.RegisterWorkbook(workbook, $"ROT:{displayName}");
                        if (context.IsConfiguredWorkbook(workbook))
                        {
                            runningObject = null;
                            return workbook;
                        }
                    }
                    else if (runningObject is ExcelInterop.Application excelApplication)
                    {
                        var found = TryFindInApplication(excelApplication, context, $"ROT:{displayName}");
                        if (found is not null)
                        {
                            return found;
                        }
                    }
                    else if (runningObject is ExcelInterop.Window window)
                    {
                        ExcelInterop.Application? windowApplication = null;
                        try
                        {
                            windowApplication = window.Application;
                            var found = TryFindInApplication(windowApplication, context, $"ROT-Window:{displayName}");
                            if (found is not null)
                            {
                                return found;
                            }
                        }
                        finally
                        {
                            ComObject.FinalRelease(windowApplication);
                        }
                    }
                }
                catch (Exception ex)
                {
                    context.AddProbeError("掃描 ROT 物件失敗", ex);
                }
                finally
                {
                    ComObject.FinalRelease(monikers[0]);
                    monikers[0] = null!;
                    ComObject.FinalRelease(runningObject);
                }
            }
        }
        catch (Exception ex)
        {
            context.AddProbeError("取得 Running Object Table 失敗", ex);
        }
        finally
        {
            ComObject.FinalRelease(enumMoniker);
            ComObject.FinalRelease(bindContext);
            ComObject.FinalRelease(runningObjectTable);
        }

        return null;
    }

    private static ExcelInterop.Workbook? TryFindFromActiveExcel(ResolutionContext context)
    {
        object? activeObject = null;
        ExcelInterop.Application? application = null;
        try
        {
            var clsid = new Guid("00024500-0000-0000-C000-000000000046");
            GetActiveObject(ref clsid, IntPtr.Zero, out activeObject);
            application = activeObject as ExcelInterop.Application;
            if (application is null)
            {
                return null;
            }

            activeObject = null;
            return TryFindInApplication(application, context, "GetActiveObject");
        }
        catch (Exception ex)
        {
            context.AddProbeError("GetActiveObject 無法取得 Excel", ex);
            return null;
        }
        finally
        {
            ComObject.FinalRelease(application);
            ComObject.FinalRelease(activeObject);
        }
    }

    private static ExcelInterop.Workbook? TryFindFromExcelWindows(ResolutionContext context)
    {
        var excelDocumentWindows = EnumerateExcelDocumentWindows(context);
        foreach (var windowHandle in excelDocumentWindows)
        {
            object? nativeObject = null;
            ExcelInterop.Application? application = null;
            try
            {
                var iid = IidDispatch;
                var hresult = AccessibleObjectFromWindow(windowHandle, ObjIdNativeOm, ref iid, out nativeObject);
                if (hresult != 0 || nativeObject is null)
                {
                    context.AddNativeObjectFailure(hresult);
                    continue;
                }

                context.NativeObjectSuccessCount++;
                if (nativeObject is ExcelInterop.Application directApplication)
                {
                    application = directApplication;
                    nativeObject = null;
                }
                else if (nativeObject is ExcelInterop.Window excelWindow)
                {
                    application = excelWindow.Application;
                }

                if (application is null)
                {
                    continue;
                }

                var found = TryFindInApplication(application, context, $"ExcelWindow:0x{windowHandle.ToInt64():X}");
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception ex)
            {
                context.AddProbeError($"由 Excel 視窗 0x{windowHandle.ToInt64():X} 取得自動化介面失敗", ex);
            }
            finally
            {
                ComObject.FinalRelease(application);
                ComObject.FinalRelease(nativeObject);
            }
        }

        return null;
    }

    /// <summary>
    /// 找不到已開啟的活頁簿時，自動用 Excel 開啟指定檔案。優先重用既有的 Excel Application
    /// （避免多開一個 Excel 處理程序），找不到才啟動新的 Excel。
    /// </summary>
    private static ExcelInterop.Workbook? TryAutoOpenWorkbook(string fullPath, ResolutionContext context)
    {
        ExcelInterop.Application? application = null;
        var ownsApplication = false;
        try
        {
            application = TryGetRunningApplication(context);
            if (application is null)
            {
                application = new ExcelInterop.Application();
                ownsApplication = true;
            }

            application.Visible = true;

            ExcelInterop.Workbooks? workbooks = null;
            ExcelInterop.Workbook? workbook = null;
            try
            {
                workbooks = application.Workbooks;
                workbook = workbooks.Open(fullPath);
                context.RegisterWorkbook(workbook, "AutoOpen");
                var result = workbook;
                workbook = null;
                application = null;
                return result;
            }
            finally
            {
                ComObject.FinalRelease(workbook);
                ComObject.FinalRelease(workbooks);
            }
        }
        catch (Exception ex)
        {
            context.AddProbeError("自動開啟 Excel 活頁簿失敗", ex);
            if (ownsApplication)
            {
                try
                {
                    application?.Quit();
                }
                catch
                {
                    // 開啟失敗後嘗試關閉自行啟動的 Excel，失敗不應覆蓋原始錯誤。
                }
            }

            return null;
        }
        finally
        {
            ComObject.FinalRelease(application);
        }
    }

    private static ExcelInterop.Application? TryGetRunningApplication(ResolutionContext context)
    {
        object? activeObject = null;
        try
        {
            var clsid = new Guid("00024500-0000-0000-C000-000000000046");
            GetActiveObject(ref clsid, IntPtr.Zero, out activeObject);
            if (activeObject is ExcelInterop.Application application)
            {
                activeObject = null;
                return application;
            }

            return null;
        }
        catch (Exception ex)
        {
            context.AddProbeError("GetActiveObject 無法取得既有 Excel 供自動開啟使用", ex);
            return null;
        }
        finally
        {
            ComObject.FinalRelease(activeObject);
        }
    }

    private static ExcelInterop.Workbook? TryFindInApplication(
        ExcelInterop.Application application,
        ResolutionContext context,
        string source)
    {
        ExcelInterop.Workbooks? workbooks = null;
        try
        {
            context.ApplicationProbeCount++;
            workbooks = application.Workbooks;
            var count = workbooks.Count;
            for (var index = 1; index <= count; index++)
            {
                ExcelInterop.Workbook? workbook = null;
                try
                {
                    workbook = (ExcelInterop.Workbook)workbooks[index];
                    context.RegisterWorkbook(workbook, source);
                    if (context.IsConfiguredWorkbook(workbook))
                    {
                        var result = workbook;
                        workbook = null;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    context.AddProbeError($"讀取 {source} 的第 {index} 本活頁簿失敗", ex);
                }
                finally
                {
                    ComObject.FinalRelease(workbook);
                }
            }
        }
        catch (Exception ex)
        {
            context.AddProbeError($"列舉 {source} 的 Workbooks 失敗", ex);
        }
        finally
        {
            ComObject.FinalRelease(workbooks);
        }

        InspectProtectedViewWindows(application, context, source);
        return null;
    }

    private static void InspectProtectedViewWindows(
        ExcelInterop.Application application,
        ResolutionContext context,
        string source)
    {
        ExcelInterop.ProtectedViewWindows? protectedViewWindows = null;
        try
        {
            protectedViewWindows = application.ProtectedViewWindows;
            var count = protectedViewWindows.Count;
            for (var index = 1; index <= count; index++)
            {
                ExcelInterop.ProtectedViewWindow? protectedWindow = null;
                try
                {
                    protectedWindow = (ExcelInterop.ProtectedViewWindow)protectedViewWindows[index];
                    var candidate = CombineProtectedViewPath(protectedWindow.SourcePath, protectedWindow.SourceName);
                    context.RegisterProtectedView(candidate, source);
                }
                catch (Exception ex)
                {
                    context.AddProbeError($"讀取 {source} 的第 {index} 個受保護檢視視窗失敗", ex);
                }
                finally
                {
                    ComObject.FinalRelease(protectedWindow);
                }
            }
        }
        catch (Exception ex)
        {
            context.AddProbeError($"列舉 {source} 的 ProtectedViewWindows 失敗", ex);
        }
        finally
        {
            ComObject.FinalRelease(protectedViewWindows);
        }
    }

    private static string CombineProtectedViewPath(string? sourcePath, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return sourceName?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return sourcePath.Trim();
        }

        var path = sourcePath.Trim();
        var name = sourceName.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var baseUri) && !baseUri.IsFile)
        {
            var normalizedBase = path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
            return new Uri(new Uri(normalizedBase), name).AbsoluteUri;
        }

        try
        {
            return Path.Combine(path, name);
        }
        catch
        {
            return $"{path.TrimEnd('\\', '/')}{Path.DirectorySeparatorChar}{name}";
        }
    }

    private static IReadOnlyList<IntPtr> EnumerateExcelDocumentWindows(ResolutionContext context)
    {
        var result = new HashSet<IntPtr>();
        EnumWindows((topLevelWindow, _) =>
        {
            if (!string.Equals(GetWindowClassName(topLevelWindow), "XLMAIN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            context.ExcelTopLevelWindowCount++;
            EnumChildWindows(topLevelWindow, (childWindow, _) =>
            {
                if (string.Equals(GetWindowClassName(childWindow), "EXCEL7", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(childWindow);
                }

                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);

        context.ExcelDocumentWindowCount = result.Count;
        return result.ToArray();
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(windowHandle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private sealed class ResolutionContext
    {
        private readonly List<string> _probeErrors = [];
        private readonly HashSet<string> _protectedViewPaths = new(StringComparer.OrdinalIgnoreCase);

        public ResolutionContext(string configuredFullPath)
        {
            ConfiguredFullPath = configuredFullPath;
        }

        public string ConfiguredFullPath { get; }
        public HashSet<string> OpenWorkbookPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ProtectedViewMatch { get; private set; }
        public int RotMonikerCount { get; set; }
        public int RotObjectCount { get; set; }
        public int ApplicationProbeCount { get; set; }
        public int ExcelTopLevelWindowCount { get; set; }
        public int ExcelDocumentWindowCount { get; set; }
        public int NativeObjectSuccessCount { get; set; }
        public int NativeObjectFailureCount { get; private set; }

        public bool IsConfiguredWorkbook(ExcelInterop.Workbook workbook)
        {
            try
            {
                return ExcelWorkbookPathMatcher.IsSamePath(workbook.FullName, ConfiguredFullPath);
            }
            catch (Exception ex)
            {
                AddProbeError("讀取 Workbook.FullName 失敗", ex);
                return false;
            }
        }

        public void RegisterWorkbook(ExcelInterop.Workbook workbook, string source)
        {
            try
            {
                var fullName = workbook.FullName;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    OpenWorkbookPaths.Add(fullName);
                }
            }
            catch (Exception ex)
            {
                AddProbeError($"由 {source} 讀取 Workbook.FullName 失敗", ex);
            }
        }

        public void RegisterProtectedView(string candidate, string source)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            _protectedViewPaths.Add(candidate);
            if (ExcelWorkbookPathMatcher.IsSamePath(candidate, ConfiguredFullPath)
                || ExcelWorkbookPathMatcher.HasSameFileName(candidate, ConfiguredFullPath))
            {
                ProtectedViewMatch = true;
            }
        }

        public void AddNativeObjectFailure(int hresult)
        {
            NativeObjectFailureCount++;
            if (hresult != 0)
            {
                _probeErrors.Add($"AccessibleObjectFromWindow 失敗：0x{hresult:X8}");
            }
        }

        public void AddProbeError(string stage, Exception exception)
        {
            if (_probeErrors.Count >= 20)
            {
                return;
            }

            _probeErrors.Add($"{stage}：{exception.GetType().Name} 0x{exception.HResult:X8}－{exception.Message}");
        }

        public string BuildDiagnosticMessage()
        {
            var builder = new StringBuilder();
            builder.AppendLine("Excel 活頁簿連線診斷：");
            builder.AppendLine($"- 設定路徑：{ConfiguredFullPath}");
            builder.AppendLine($"- ROT Moniker／物件：{RotMonikerCount}／{RotObjectCount}");
            builder.AppendLine($"- Excel Application 探測次數：{ApplicationProbeCount}");
            builder.AppendLine($"- Excel 主視窗／文件視窗：{ExcelTopLevelWindowCount}／{ExcelDocumentWindowCount}");
            builder.AppendLine($"- NativeOM 成功／失敗：{NativeObjectSuccessCount}／{NativeObjectFailureCount}");
            builder.AppendLine($"- 受保護檢視命中：{ProtectedViewMatch}");
            builder.AppendLine("- 已偵測活頁簿：" +
                               (OpenWorkbookPaths.Count == 0
                                   ? "（無）"
                                   : string.Join("；", OpenWorkbookPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))));
            if (_protectedViewPaths.Count > 0)
            {
                builder.AppendLine("- 受保護檢視檔案：" + string.Join("；", _protectedViewPaths));
            }

            if (_probeErrors.Count > 0)
            {
                builder.AppendLine("- 探測錯誤：");
                foreach (var error in _probeErrors)
                {
                    builder.AppendLine($"  * {error}");
                }
            }

            return builder.ToString().TrimEnd();
        }
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object activeObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumFunction, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parentWindow, EnumWindowsProc enumFunction, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maxCount);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr windowHandle,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object nativeObject);
}
