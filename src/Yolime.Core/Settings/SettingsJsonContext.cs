using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yolime.Core.Settings;

// Comments and trailing commas are allowed because the spec makes this file the "Advanced" settings UI.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(YolimeSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
