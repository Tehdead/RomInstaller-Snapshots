using System;
using System.Runtime.InteropServices;

// Keep COM types namespaced to avoid collisions with System.IO, etc.
using Wsh = IWshRuntimeLibrary;

namespace RomInstaller.Core.Services;
// Build note:
// Make sure RomInstaller.Core.csproj includes either:
//  - a NuGet package providing IWshRuntimeLibrary interop, OR
//  - a COM reference to "Windows Script Host Object Model" (wshom.ocx), e.g.:
//    <ItemGroup>
//      <COMReference Include="IWshRuntimeLibrary">
//        <Guid>{F935DC20-1CF0-11D0-ADB9-00C04FD58A0B}</Guid>
//        <VersionMajor>1</VersionMajor>
//        <VersionMinor>0</VersionMinor>
//        <WrapperTool>tlbimp</WrapperTool>
//        <Lcid>0</Lcid>
//        <Isolated>false</Isolated>
//      </COMReference>
//    </ItemGroup>

/// <summary>
/// ShortcutService
/// ---------------
/// Creates Windows `.lnk` shortcuts for installed games and (best-effort) sets
/// the AppUserModelID so Windows groups taskbar items under our app identity.
///
/// Design goals:
///   • Simple, deterministic API (one public entry point)
///   • No COM types leaking outside this class
///   • Fail-safe: logging on exceptions, never crash the caller
/// </summary>

public static class ShortcutService
{
    /// <summary>
    /// Try to read a .lnk file and extract its TargetPath and Arguments.
    /// Returns false if the shortcut cannot be read.
    /// </summary>
    public static bool TryReadShortcut(string lnkPath, out string? targetPath, out string? arguments)
    {
        targetPath = null;
        arguments = null;

        try
        {
            if (string.IsNullOrWhiteSpace(lnkPath) || !System.IO.File.Exists(lnkPath))
                return false;

            var shell = new Wsh.WshShell();
            var lnk = (Wsh.IWshShortcut)shell.CreateShortcut(lnkPath);
            targetPath = lnk.TargetPath;
            arguments = lnk.Arguments;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception("ShortcutService.TryReadShortcut", ex);
            return false;
        }
    }
    /// <summary>
    /// Creates a desktop shortcut pointing to the Launcher with given arguments.
    ///
    /// Parameters:
    ///   shortcutName   Friendly display name (used as .lnk filename; sanitized)
    ///   launcherExe    Absolute path to RomInstaller.Launcher.exe
    ///   arguments      Command line (e.g., "--key <manifestId>")
    ///   iconPath       Optional icon path; used if file exists
    ///   appUserModelId Optional AUMID; used for taskbar grouping
    ///
    /// Returns: Full path to the created .lnk file.
    ///
    /// Notes:
    ///   • Uses WSH (Windows Script Host) COM to build `.lnk`
    ///   • Attempts to set AppUserModelID via shell property store (best-effort)
    /// </summary>
    public static string CreateDesktopShortcut(
        string shortcutName,
        string launcherExe,
        string arguments,
        string? iconPath,
        string? appUserModelId)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnkPath = System.IO.Path.Combine(desktop, $"{Sanitize(shortcutName)}.lnk");

        System.IO.Directory.CreateDirectory(desktop);

        // Create the .lnk via WSH COM
        var shell = new Wsh.WshShell();
        var lnk = (Wsh.IWshShortcut)shell.CreateShortcut(lnkPath);
        lnk.TargetPath = launcherExe;
        lnk.Arguments = arguments;
        lnk.WorkingDirectory = System.IO.Path.GetDirectoryName(launcherExe)!;

        if (!string.IsNullOrWhiteSpace(iconPath) && System.IO.File.Exists(iconPath))
            lnk.IconLocation = iconPath;

        lnk.Save();

        // Best-effort: set AppUserModelID (non-fatal if COM is unavailable)
        if (!string.IsNullOrWhiteSpace(appUserModelId))
            NativePropertyStore.TrySetAppUserModelID(lnkPath, appUserModelId);

        return lnkPath;
    }

    /// <summary>
    /// Sanitizes file names so Windows will accept them (replace invalid chars with '_').
    /// </summary>
    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>
    /// Encapsulates Shell Property Store interop to set AppUserModelID on a .lnk file.
    /// This keeps COM details private and prevents accessibility issues with nested types.
    ///
    /// Implementation notes:
    ///   • Uses `SHGetPropertyStoreFromParsingName` to obtain IPropertyStore
    ///   • Writes `PKEY_AppUserModel_ID` (VT_LPWSTR) to the store
    ///   • Swallows exceptions (logs via Logger) to remain non-fatal
    /// </summary>
    private static class NativePropertyStore
    {
        // ---------- Public entry point ----------

        /// <summary>
        /// Safely sets the AppUserModelID on a given shortcut file.
        /// Non-fatal on any failure (logs and returns).
        /// </summary>
        public static void TrySetAppUserModelID(string lnkPath, string appId)
        {
            try
            {
                var store = GetPropertyStoreForFile(lnkPath);
                if (store is null) return;

                // Copy static readonly key into a local so we can pass by ref
                var key = PKEY_AppUserModel_ID;
                SetString(store, ref key, appId);
                Commit(store);

                Marshal.ReleaseComObject(store);
            }
            catch (Exception ex)
            {
                Logger.Exception("TrySetAppUserModelID", ex); // non-fatal by design
            }
        }

        // ---------- Constants / PROPERTYKEYs ----------

        // PKEY_AppUserModel_ID: fmtid {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5
        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        // ---------- COM interop definitions ----------

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            uint GetCount(out uint cProps);
            uint GetAt(uint iProp, out PROPERTYKEY pkey);
            uint GetValue(ref PROPERTYKEY key, out PropVariant pv);
            uint SetValue(ref PROPERTYKEY key, ref PropVariant pv);
            uint Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            uint sfgaoIn,
            [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        // ---------- Private helpers ----------

        /// <summary>
        /// Retrieves the property store for a file path, or null on failure.
        /// </summary>
        private static IPropertyStore? GetPropertyStoreForFile(string path)
        {
            try
            {
                // Do not pass ref to static readonly Guid — make a local copy
                var iid = IID_IPropertyStore;
                SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store);
                return store;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes a string property into the store under the given PROPERTYKEY.
        /// </summary>
        private static void SetString(IPropertyStore store, ref PROPERTYKEY key, string value)
        {
            var pv = PropVariant.FromString(value);
            store.SetValue(ref key, ref pv);
            pv.Clear(); // ensure we free the allocated LPWSTR
        }

        /// <summary>Commits outstanding changes to the property store.</summary>
        private static void Commit(IPropertyStore store) => store.Commit();

        /// <summary>
        /// Minimal `PROPVARIANT` struct supporting only string payloads (VT_LPWSTR).
        /// Responsible for allocating and freeing memory for the string.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] private ushort vt;   // VARTYPE
            [FieldOffset(8)] private IntPtr ptr;  // LPWSTR

            public static PropVariant FromString(string value)
            {
                var pv = new PropVariant { vt = 31 /* VT_LPWSTR */ };
                pv.ptr = Marshal.StringToCoTaskMemUni(value);
                return pv;
            }

            public void Clear()
            {
                if (vt == 31 && ptr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(ptr);
                vt = 0;
                ptr = IntPtr.Zero;
            }
        }
    }
}
