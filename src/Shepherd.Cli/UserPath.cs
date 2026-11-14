using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Shepherd.Cli;

/// <summary>Edits the per-user PATH (HKCU\\Environment), like the "Environment Variables" dialog.</summary>
internal static partial class UserPath
{
    /// <returns>True if PATH changed.</returns>
    public static bool Edit(string dir, bool add)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        if (key is null) return false;
        var exists = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase);
        var current = exists ? key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "" : "";
        // Keep the existing value type (usually REG_EXPAND_SZ); a new value is REG_EXPAND_SZ.
        var kind = exists ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var has = parts.Any(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (add == has) return false;
        if (add) parts.Add(dir);
        else parts.RemoveAll(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        key.SetValue("Path", string.Join(';', parts), kind);
        Broadcast();
        return true;
    }

    // Tell running programs (Explorer, new terminals) that the environment changed.
    private static void Broadcast()
    {
        _ = SendMessageTimeoutW(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
