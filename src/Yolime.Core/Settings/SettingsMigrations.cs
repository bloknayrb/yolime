using System.Text.Json.Nodes;

namespace Yolime.Core.Settings;

/// <summary>One step per schema version: <c>steps[n]</c> turns a version-n document into version n+1.</summary>
public static class SettingsMigrations
{
    internal static readonly IReadOnlyDictionary<int, Action<JsonObject>> Steps = new Dictionary<int, Action<JsonObject>>();

    public static JsonObject Migrate(JsonObject doc, int from) => Migrate(doc, from, SettingsStore.CurrentSchemaVersion, Steps);

    internal static JsonObject Migrate(JsonObject doc, int from, int to, IReadOnlyDictionary<int, Action<JsonObject>> steps)
    {
        for (var v = from; v < to; v++)
        {
            if (steps.TryGetValue(v, out var step)) step(doc);
        }
        doc["schemaVersion"] = to;
        return doc;
    }
}
