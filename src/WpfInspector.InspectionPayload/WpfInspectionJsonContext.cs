using System.Text.Json.Serialization;
using WpfInspector.Core.Models;

namespace WpfInspector.InspectionPayload;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(WpfInspectionResult))]
public sealed partial class WpfInspectionJsonContext : JsonSerializerContext
{
}
