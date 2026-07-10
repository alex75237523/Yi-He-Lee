using System.Security.Principal;

namespace YiHeLee.App.Infrastructure;

/// <summary>程式一律以一般權限執行；此處僅供偵測目前是否誤以系統管理員身分啟動。</summary>
internal static class AdministratorHelper
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
