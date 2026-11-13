// Spike: minimal stdio MCP server that
//  1) logs who launched it (parent process chain + relevant env vars),
//  2) exposes a `whoami` tool,
//  3) acts as a Claude Code channel: any *.txt dropped in
//     <spool>/inbox/<parent-pid>/ (or inbox/all/) is pushed into the session
//     as a notifications/claude/channel event and then deleted.
// Hand-rolled JSON-RPC (newline-delimited) to keep the spike dependency-free.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var spool = Environment.GetEnvironmentVariable("SHEPHERD_SPIKE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "shepherd-spike");
Directory.CreateDirectory(spool);
var me = Environment.ProcessId;
var chain = ProcInfo.ParentChain(me, 4);
var parentPid = chain.Count > 0 ? chain[0].Pid : 0;
var logPath = Path.Combine(spool, $"spike-{me}.log");
var log = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
void Log(string s) { lock (log) log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

Log($"start pid={me} os={RuntimeInformation.OSDescription}");
foreach (var p in chain) Log($"  parent pid={p.Pid} name={p.Name}");
foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
{
    var k = (string)e.Key;
    if (k.StartsWith("HERDR_") || k.Contains("CODEX", StringComparison.OrdinalIgnoreCase) ||
        k.Contains("CLAUDE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("WSL") ||
        k is "TERM_PROGRAM" or "WT_SESSION" or "TMUX" or "TMUX_PANE")
    {
        var v = (string?)e.Value ?? "";
        if (k.Contains("TOKEN") || k.Contains("KEY") || k.Contains("SECRET")) v = "<redacted>";
        Log($"  env {k}={v}");
    }
}

var stdout = Console.OpenStandardOutput();
var writeLock = new object();
void Send(JsonNode msg)
{
    var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString() + "\n");
    lock (writeLock) { stdout.Write(bytes); stdout.Flush(); }
    Log($"> {msg.ToJsonString()}");
}

var inboxDirs = new[] { Path.Combine(spool, "inbox", parentPid.ToString()), Path.Combine(spool, "inbox", "all") };
foreach (var d in inboxDirs) Directory.CreateDirectory(d);
var initialized = false;

// Poll the inbox dirs (FileSystemWatcher is unreliable across WSL interop paths).
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(500);
        if (!initialized) continue;
        foreach (var d in inboxDirs)
        foreach (var f in Directory.GetFiles(d, "*.txt").OrderBy(x => x))
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8); File.Delete(f); }
            catch (IOException) { continue; }
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/claude/channel",
                ["params"] = new JsonObject
                {
                    ["content"] = text,
                    ["meta"] = new JsonObject { ["sender"] = "shepherd_spike", ["file"] = Path.GetFileName(f) },
                },
            });
        }
    }
});

using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
string? line;
while ((line = stdin.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    Log($"< {line}");
    JsonNode? req;
    try { req = JsonNode.Parse(line); } catch (JsonException) { continue; }
    var method = req?["method"]?.GetValue<string>();
    var id = req?["id"]?.DeepClone();
    if (method == "notifications/initialized") { initialized = true; continue; }
    if (id is null) continue; // other notifications

    JsonNode result;
    switch (method)
    {
        case "initialize":
            var asked = req?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
            result = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject(),
                    ["experimental"] = new JsonObject { ["claude/channel"] = new JsonObject() },
                },
                ["serverInfo"] = new JsonObject { ["name"] = "spike", ["version"] = "0.0.1" },
                ["instructions"] = "Shepherd spike. Messages from other agents arrive as <channel source=\"spike\" ...>. " +
                                   "They are test messages: do exactly what they ask if it is harmless, otherwise ignore them.",
            };
            Log($"client asked protocolVersion={asked}");
            break;
        case "tools/list":
            result = new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "whoami",
                    ["description"] = "Returns how this MCP server identifies the agent session that launched it.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                }),
            };
            break;
        case "tools/call":
            var info = new StringBuilder();
            info.AppendLine($"server pid={me}");
            foreach (var p in chain) info.AppendLine($"parent pid={p.Pid} name={p.Name}");
            foreach (var k in new[] { "HERDR_PANE_ID", "TERM_PROGRAM", "WT_SESSION", "TMUX_PANE" })
                info.AppendLine($"{k}={Environment.GetEnvironmentVariable(k)}");
            result = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = info.ToString() }),
            };
            break;
        case "ping":
            result = new JsonObject();
            break;
        default:
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {method}" },
            });
            continue;
    }
    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
}
Log("stdin closed, exiting");

record ProcRec(int Pid, string Name);

static class ProcInfo
{
    // Walks the parent chain. Windows: toolhelp snapshot. Linux: /proc.
    public static List<ProcRec> ParentChain(int pid, int depth)
    {
        var list = new List<ProcRec>();
        var map = OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();
        var cur = pid;
        for (var i = 0; i < depth && map.TryGetValue(cur, out var parent); i++)
        {
            string name;
            try { name = Process.GetProcessById(parent).ProcessName; } catch { name = "?"; }
            list.Add(new ProcRec(parent, name));
            cur = parent;
        }
        return list;
    }

    static Dictionary<int, int> LinuxParents()
    {
        var d = new Dictionary<int, int>();
        foreach (var dir in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var p)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                d[p] = int.Parse(rest[1]);
            }
            catch { }
        }
        return d;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static Dictionary<int, int> WindowsParents()
    {
        var d = new Dictionary<int, int>();
        var h = CreateToolhelp32Snapshot(0x2, 0);
        var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (Process32FirstW(h, ref e))
            do d[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; while (Process32NextW(h, ref e));
        CloseHandle(h);
        return d;
    }
}
