using System.Runtime.InteropServices;

namespace YiHeLee.Infrastructure.Excel;

internal static class ComObject
{
    public static void FinalRelease(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // 釋放失敗不可覆蓋真正的業務例外。
            }
        }
    }
}
