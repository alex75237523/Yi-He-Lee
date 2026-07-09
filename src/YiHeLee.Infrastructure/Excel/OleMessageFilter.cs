using System.Runtime.InteropServices;

namespace YiHeLee.Infrastructure.Excel;

/// <summary>讓 Office COM 在忙碌時回覆稍後重試，而不是立即讓程式崩潰。</summary>
internal static class OleMessageFilter
{
    public static void Register() => NativeMethods.CoRegisterMessageFilter(new MessageFilter(), out _);
    public static void Revoke() => NativeMethods.CoRegisterMessageFilter(null, out _);

    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);

        [PreserveSig]
        int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
    }

    private sealed class MessageFilter : IMessageFilter
    {
        public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) => 0;

        public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType)
            => rejectType == 2 ? 250 : -1;

        public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) => 2;
    }

    private static class NativeMethods
    {
        [DllImport("Ole32.dll")]
        internal static extern int CoRegisterMessageFilter(IMessageFilter? newFilter, out IMessageFilter? oldFilter);
    }
}
