using System.Text;
using System.Text.Json.Nodes;

namespace WpfInspector.Protocol;

internal static class PipeProtocol
{
    internal const string Version = "wpfinspector.rpc.v1";
    private const string Prefix = "RPC:";

    internal static string EncodeRequest(PipeRequest request) =>
        EncodeNode(new JsonObject
        {
            ["Version"] = request.Version,
            ["RequestId"] = request.RequestId,
            ["Operation"] = request.Operation,
            ["Arguments"] = request.Arguments.DeepClone()
        });

    internal static string EncodeResponse(PipeResponse response) =>
        EncodeNode(new JsonObject
        {
            ["Version"] = response.Version,
            ["RequestId"] = response.RequestId is null ? null : JsonValue.Create(response.RequestId),
            ["Success"] = response.Success,
            ["ErrorCode"] = response.ErrorCode is null ? null : JsonValue.Create(response.ErrorCode),
            ["ErrorMessage"] = response.ErrorMessage is null ? null : JsonValue.Create(response.ErrorMessage),
            ["Data"] = response.Data?.DeepClone()
        });

    internal static bool TryDecodeRequest(string? payload, out PipeRequest? request)
    {
        request = null;
        if (!TryParseObject(payload, out var json))
        {
            return false;
        }
        ArgumentNullException.ThrowIfNull(json);

        var operation = json["Operation"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(operation))
        {
            return false;
        }

        request = new PipeRequest
        {
            Version = json["Version"]?.GetValue<string>() ?? Version,
            RequestId = json["RequestId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            Operation = operation,
            Arguments = json["Arguments"] as JsonObject ?? []
        };
        return true;
    }

    internal static bool TryDecodeResponse(string? payload, out PipeResponse? response)
    {
        response = null;
        if (!TryParseObject(payload, out var json))
        {
            return false;
        }
        ArgumentNullException.ThrowIfNull(json);

        var success = json["Success"]?.GetValue<bool>();
        if (success is null)
        {
            return false;
        }

        response = new PipeResponse
        {
            Version = json["Version"]?.GetValue<string>() ?? Version,
            RequestId = json["RequestId"]?.GetValue<string>(),
            Success = success.Value,
            ErrorCode = json["ErrorCode"]?.GetValue<string>(),
            ErrorMessage = json["ErrorMessage"]?.GetValue<string>(),
            Data = json["Data"]?.DeepClone()
        };
        return true;
    }

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
        node?.ToJsonString() ?? "null";

    private static string EncodeNode(JsonObject json) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));

    private static bool TryParseObject(string? payload, out JsonObject? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload[Prefix.Length..]));
            value = JsonNode.Parse(json) as JsonObject;
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
