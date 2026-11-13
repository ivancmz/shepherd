using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Shepherd.Broker;

/// <summary>Append-only monthly JSONL audit trail (ADR-0004). Full content: these are the user's own agents.</summary>
internal sealed class AuditLog
{
    private readonly string _dir;
    private readonly Lock _lock = new();

    public AuditLog(string dir) => _dir = dir;

    public void Write(string evt, JsonObject data)
    {
        data["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        data["event"] = evt;
        var path = Path.Combine(_dir, $"audit-{DateTime.UtcNow:yyyy-MM}.jsonl");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, data.ToJsonString() + "\n", Encoding.UTF8);
            }
            catch (IOException)
            {
                // Auditing must never break message delivery.
            }
        }
    }
}
