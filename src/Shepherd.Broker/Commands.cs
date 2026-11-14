using System.Diagnostics;
using System.Text;

namespace Shepherd.Broker;

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs external commands (codex, herdr, wsl). Replaced by a fake in tests.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

public static class BrokerRunners
{
    /// <summary>The real runner (no shell; arguments passed verbatim).</summary>
    public static ICommandRunner Process() => new ProcessRunner();
}

internal sealed class ProcessRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new CommandResult(-1, "", $"timeout after {timeout.TotalSeconds:0} s");
        }
        return new CommandResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

/// <summary>Locates the external tools the wake-ups need.</summary>
public static class ToolPaths
{
    /// <summary>
    /// <c>SHEPHERD_CODEX</c>, else <c>codex.exe</c> on PATH, else the native binary inside the npm
    /// package next to <c>codex.cmd</c> (npm on Windows installs a .cmd shim that
    /// <see cref="Process"/> cannot start without cmd.exe).
    /// </summary>
    public static string? Codex()
    {
        var env = Environment.GetEnvironmentVariable("SHEPHERD_CODEX");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (!OperatingSystem.IsWindows()) return FindOnPath("codex");
        if (FindOnPath("codex.exe") is { } exe) return exe;
        if (FindOnPath("codex.cmd") is { } cmd)
        {
            var native = Path.Combine(Path.GetDirectoryName(cmd)!, "node_modules", "@openai", "codex", "node_modules",
                "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            if (File.Exists(native)) return native;
        }
        return null;
    }

    /// <summary><c>HERDR_BIN_PATH</c> (set inside herdr panes), else <c>herdr</c> on PATH.</summary>
    public static string? Herdr()
    {
        var env = Environment.GetEnvironmentVariable("HERDR_BIN_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        return FindOnPath(OperatingSystem.IsWindows() ? "herdr.exe" : "herdr");
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }
}
