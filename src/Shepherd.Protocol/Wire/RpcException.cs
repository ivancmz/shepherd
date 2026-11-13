using System.Text.Json.Nodes;

namespace Shepherd.Protocol.Wire;

/// <summary>JSON-RPC error codes: standard ones plus Shepherd's own range (ADR-0005).</summary>
public static class RpcErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int Unauthorized = -32001;
    public const int NameTaken = -32002;
    public const int UnknownAgent = -32003;
    public const int ProtocolMismatch = -32004;
    public const int TooLarge = -32005;
    public const int NotRegistered = -32006;
    public const int InvalidName = -32007;
    public const int NotFound = -32008;
    public const int BrokerUnavailable = -32010;
}

public sealed class RpcException : Exception
{
    public int Code { get; }
    public JsonNode? ErrorData { get; }

    public RpcException(int code, string message, JsonNode? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }
}
