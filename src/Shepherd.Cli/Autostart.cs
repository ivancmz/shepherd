using Microsoft.Win32;

namespace Shepherd.Cli;

/// <summary>
/// Optional broker start at logon (ADR-0004), so WSL agents find it even before any Windows agent
/// starts. A per-user Run entry: no administrator rights, no Windows service.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "shepherd-broker";

    public static void Enable(string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\" broker start");
        Console.WriteLine($"broker will start at logon ({ValueName})");
    }

    public static void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is null) return;
        key.DeleteValue(ValueName);
        Console.WriteLine($"logon start removed ({ValueName})");
    }
}
