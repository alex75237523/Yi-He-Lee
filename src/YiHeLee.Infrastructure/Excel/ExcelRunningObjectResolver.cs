using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ExcelInterop = Microsoft.Office.Interop.Excel;

namespace YiHeLee.Infrastructure.Excel;

internal static class ExcelRunningObjectResolver
{
    public static ExcelInterop.Workbook FindOpenWorkbook(string workbookPath)
    {
        var fullPath = Path.GetFullPath(workbookPath);
        ExcelInterop.Workbook? resolved = TryFindFromRunningObjectTable(fullPath);
        if (resolved is not null)
        {
            return resolved;
        }

        resolved = TryFindFromActiveExcel(fullPath);
        if (resolved is not null)
        {
            return resolved;
        }

        throw new InvalidOperationException(
            "找不到已開啟的指定 Excel 活頁簿。請確認檔案已用桌面版 Excel 開啟，且 Excel 與 Yi He Lee 使用相同權限層級；若本程式以管理員執行，Excel 也必須以管理員執行。");
    }

    private static ExcelInterop.Workbook? TryFindFromRunningObjectTable(string fullPath)
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
                    monikers[0].GetDisplayName(bindContext, null, out var displayName);
                    if (!DisplayNameCouldMatch(displayName, fullPath))
                    {
                        continue;
                    }

                    runningObjectTable.GetObject(monikers[0], out runningObject);
                    if (runningObject is ExcelInterop.Workbook workbook && IsSamePath(workbook.FullName, fullPath))
                    {
                        // 回傳的 Workbook 由呼叫端釋放；避免 finally 提前釋放同一個 RCW。
                        runningObject = null;
                        return workbook;
                    }
                }
                catch
                {
                    // 某些 ROT 物件無權限或已失效，繼續搜尋下一個。
                }
                finally
                {
                    ComObject.FinalRelease(monikers[0]);
                    ComObject.FinalRelease(runningObject);
                }
            }
        }
        finally
        {
            ComObject.FinalRelease(enumMoniker);
            ComObject.FinalRelease(bindContext);
            ComObject.FinalRelease(runningObjectTable);
        }

        return null;
    }

    private static ExcelInterop.Workbook? TryFindFromActiveExcel(string fullPath)
    {
        object? activeObject = null;
        ExcelInterop.Application? application = null;
        ExcelInterop.Workbooks? workbooks = null;
        try
        {
            var clsid = new Guid("00024500-0000-0000-C000-000000000046");
            GetActiveObject(ref clsid, IntPtr.Zero, out activeObject);
            application = (ExcelInterop.Application)activeObject;
            workbooks = application.Workbooks;
            foreach (ExcelInterop.Workbook workbook in workbooks)
            {
                if (IsSamePath(workbook.FullName, fullPath))
                {
                    return workbook;
                }

                ComObject.FinalRelease(workbook);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            ComObject.FinalRelease(workbooks);
            ComObject.FinalRelease(application);
            if (activeObject is not ExcelInterop.Application)
            {
                ComObject.FinalRelease(activeObject);
            }
        }

        return null;
    }

    private static bool DisplayNameCouldMatch(string? displayName, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var normalizedDisplayName = displayName.TrimStart('!').Replace('/', '\\');
        return normalizedDisplayName.Contains(fullPath, StringComparison.OrdinalIgnoreCase)
               || normalizedDisplayName.EndsWith(Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string candidate, string fullPath)
    {
        try
        {
            return string.Equals(Path.GetFullPath(candidate), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object activeObject);
}
