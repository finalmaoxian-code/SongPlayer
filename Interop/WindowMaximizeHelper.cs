using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SongPlayer.Interop;

/// <summary>
/// Keeps a borderless (<c>WindowStyle=None</c> + <see cref="System.Windows.Shell.WindowChrome"/>)
/// window inside the monitor work area when it is maximized.
/// </summary>
/// <remarks>
/// <para>
/// WPF sizes a maximized <c>WindowChrome</c> window to the whole monitor plus an inflation
/// equal to the invisible resize border, which it pushes off-screen. That is intentional for
/// regular chrome, but for a fully custom chrome it means the client area is taller than the
/// work area, so the bottom of the window ends up underneath the taskbar and the player bar
/// is unreachable.
/// </para>
/// <para>
/// Windows asks the window for its desired maximized size through <c>WM_GETMINMAXINFO</c>
/// before maximising. Answering that message with the monitor work area is the supported way
/// to correct the bounds, and it also gets multi-monitor and DPI changes right because the
/// monitor is resolved from the window handle every time.
/// </para>
/// </remarks>
internal static class WindowMaximizeHelper
{
    private const int WmGetMinMaxInfo = 0x0024;

    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>
    /// Starts constraining <paramref name="window"/> to the work area of whichever monitor
    /// currently hosts it.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle is { } handle && handle != IntPtr.Zero)
        {
            AttachToHandle(handle);
            return;
        }

        // The HWND does not exist yet, so wait for the window to be created.
        window.SourceInitialized += (_, _) =>
            AttachToHandle(new WindowInteropHelper(window).Handle);
    }

    private static void AttachToHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        HwndSource.FromHwnd(handle)?.AddHook(Hook);
    }

    private static IntPtr Hook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled
    )
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (TryApplyWorkAreaBounds(hwnd, lParam))
        {
            // The value was corrected in place; no further default handling is wanted.
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool TryApplyWorkAreaBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var work = monitorInfo.WorkArea;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // Position is relative to the monitor origin, size is absolute: that makes the
        // maximized window cover exactly the work area, leaving the taskbar visible.
        bounds.MaxPosition.X = work.Left - monitorInfo.Monitor.Left;
        bounds.MaxPosition.Y = work.Top - monitorInfo.Monitor.Top;
        bounds.MaxSize.X = work.Right - work.Left;
        bounds.MaxSize.Y = work.Bottom - work.Top;

        Marshal.StructureToPtr(bounds, lParam, fDeleteOld: false);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
