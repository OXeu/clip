using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Clip.Desktop;

internal static class WindowPresentation
{
    private const int ExtendedStyle = -20;
    private const int DialogModalFrame = 0x00000001;
    private const uint SetIcon = 0x0080;

    internal static void HideCaptionIcon(Window window)
    {
        // Keep native resizing, caption buttons, taskbar presence and Alt+Space.
        // Retain WPF's large application icon for the taskbar and Alt+Tab.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongW(handle, ExtendedStyle, GetWindowLongW(handle, ExtendedStyle) | DialogModalFrame) == 0 &&
                Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            SendMessageW(handle, SetIcon, IntPtr.Zero, IntPtr.Zero);
            // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
            if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        };
    }

    internal static void VerifyCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || (GetWindowLongW(handle, ExtendedStyle) & DialogModalFrame) == 0 ||
            SendMessageW(handle, 0x007F, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero ||
            window.Title.Contains("Clip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Window caption still contains application branding.");
        if (SendMessageW(handle, 0x007F, new IntPtr(1), IntPtr.Zero) == IntPtr.Zero)
            throw new InvalidOperationException("Window application icon is missing.");
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    // GWL_EXSTYLE is a 32-bit value on both x86 and x64.
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowLongW(IntPtr window, int index, int value);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
