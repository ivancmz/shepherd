using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Shepherd.Protocol.Wire;

namespace Shepherd.Protocol;

public static class Json
{
    public static JsonNode ToNode<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToNode(value, typeInfo) ?? new JsonObject();

    /// <summary>Deserializes request parameters; malformed input becomes an <c>invalid_params</c> RPC error.</summary>
    public static T Params<T>(JsonNode? node, JsonTypeInfo<T> typeInfo)
    {
        if (node is null) throw new RpcException(RpcErrors.InvalidParams, "missing params");
        try
        {
            return node.Deserialize(typeInfo) ?? throw new RpcException(RpcErrors.InvalidParams, "null params");
        }
        catch (JsonException ex)
        {
            throw new RpcException(RpcErrors.InvalidParams, ex.Message);
        }
    }

    public static T Result<T>(JsonNode? node, JsonTypeInfo<T> typeInfo)
    {
        if (node is null) throw new RpcException(RpcErrors.InternalError, "empty result");
        return node.Deserialize(typeInfo) ?? throw new RpcException(RpcErrors.InternalError, "null result");
    }
}
