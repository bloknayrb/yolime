using System.Reflection;
using Yolime.Core.Logging;

namespace Yolime.Core.Tests.Logging;

/// <summary>
/// Structural guard: content can only reach a log through a string or object member, so the types that carry
/// log and timing data may not grow one. The single exception is the log key, which is validated as an identifier.
/// </summary>
public class NoStringFieldsTests
{
    private static readonly HashSet<string> Allowed = ["LogField.Key"];

    [Theory]
    [InlineData(typeof(LogField))]
    [InlineData(typeof(RunTiming))]
    public void LoggedTypes_HaveNoStringOrObjectMembers(Type root)
    {
        var offenders = new List<string>();
        Walk(root, new HashSet<Type>(), offenders);
        Assert.Empty(offenders);
    }

    private static void Walk(Type type, HashSet<Type> seen, List<string> offenders)
    {
        if (!seen.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(Guid) || type == typeof(DateTimeOffset)) return;
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in type.GetFields(all))
        {
            // Auto-property backing fields are named <Name>k__BackingField.
            var name = field.Name.StartsWith('<') ? field.Name[1..field.Name.IndexOf('>')] : field.Name;
            if (Allowed.Contains($"{type.Name}.{name}")) continue;
            var ft = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            if (ft == typeof(string) || ft == typeof(object)) offenders.Add($"{type.Name}.{name}");
            else if (ft.IsGenericType) foreach (var arg in ft.GetGenericArguments()) Walk(arg, seen, offenders);
            else if (ft.Namespace?.StartsWith("Yolime") == true) Walk(ft, seen, offenders);
        }
    }
}
