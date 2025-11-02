using System;
using System.Runtime.InteropServices;

namespace RomInstaller.Core.Services;

public static class UserNotify
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Best-effort error dialog (won’t throw).</summary>
    public static void ErrorBox(string title, string message)
    {
        try
        {
            // MB_OK | MB_ICONERROR
            MessageBoxW(IntPtr.Zero, message, title, 0x00000010);
        }
        catch { /* never crash due to UI */ }
    }
}
