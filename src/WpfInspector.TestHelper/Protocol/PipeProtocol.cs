using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WpfInspector.Protocol;

internal static class PipeProtocol
{
    internal const string Version = "wpfinspector.rpc.v1";
    private const string Prefix = "RPC:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string EncodeRequest(PipeRequest request) => Encode(request);

    internal static string EncodeResponse(PipeResponse response) => Encode(response);

    internal static bool TryDecodeRequest(string? payload, out PipeRequest? request) =>
        TryDecode(payload, out request);

    internal static bool TryDecodeResponse(string? payload, out PipeResponse? response) =>
        TryDecode(payload, out response);

    internal static PipeResponse Success(JsonNode? data = null, string? requestId = null) =>
        new()
        {
            RequestId = requestId,
            Success = true,
            Data = data
        };

    internal static PipeResponse Error(string errorCode, string errorMessage, string? requestId = null, JsonNode? data = null) =>
        new()
        {
            RequestId = requestId,
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Data = data
        };

    internal static string ToJson(JsonNode? node) =>
        node?.ToJsonString(JsonOptions) ?? "null";

    private static string Encode<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static bool TryDecode<T>(string? payload, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload[Prefix.Length..]));
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }
}

internal sealed class PipeRequest
{
    public string Version { get; init; } = PipeProtocol.Version;

    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    public string Operation { get; init; } = string.Empty;

    public JsonObject Arguments { get; init; } = [];
}

internal sealed class PipeResponse
{
    public string Version { get; init; } = PipeProtocol.Version;

    public string? RequestId { get; init; }

    public bool Success { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public JsonNode? Data { get; init; }
}
