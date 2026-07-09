using System.Diagnostics;
using System.Security.Principal;

namespace YiHeLee.App.Infrastructure;

internal static class AdministratorHelper
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestartAsAdministrator()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("無法取得程式路徑。");
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "--minimized"
        });
    }
}
