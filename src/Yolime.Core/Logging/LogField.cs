using System.Globalization;
using System.Text.RegularExpressions;

namespace Yolime.Core.Logging;

/// <summary>
/// One typed value in a log line. There is deliberately no way to build one from free text: the only string
/// is the key, which must be a short identifier, so transcripts, prompts, keys and URLs cannot reach a log.
/// </summary>
public readonly partial struct LogField
{
    private enum Kind : byte { Count, Ms, Http, Tag, Error }

    private readonly Kind _kind;
    private readonly long _value;
    private readonly Type? _type;

    public string Key { get; }

    private LogField(string key, Kind kind, long value, Type? type)
    {
        Key = key;
        _kind = kind;
        _value = value;
        _type = type;
    }

    public static LogField Count(string key, long n) => new(CheckKey(key), Kind.Count, n, null);

    public static LogField Ms(string key, long ms) => new(CheckKey(key), Kind.Ms, ms, null);

    public static LogField HttpStatus(int code) => new("http", Kind.Http, code, null);

    public static LogField Tag<T>(string key, T value) where T : struct, Enum =>
        new(CheckKey(key), Kind.Tag, Convert.ToInt64(value, CultureInfo.InvariantCulture), typeof(T));

    /// <summary>Only the exception's type and HResult: messages can quote request URLs, bodies or input.</summary>
    public static LogField Error(Exception ex) => new("error", Kind.Error, ex.HResult, ex.GetType());

    internal string Format() => _kind switch
    {
        Kind.Tag => $"{Key}={Enum.GetName(_type!, Enum.ToObject(_type!, _value)) ?? _value.ToString(CultureInfo.InvariantCulture)}",
        Kind.Error => $"error={_type!.Name} hr=0x{_value:X8}",
        _ => $"{Key}={_value.ToString(CultureInfo.InvariantCulture)}",
    };

    private static string CheckKey(string key) =>
        // The message must not echo the rejected key: a caller who passed content as a key would leak it via the exception.
        KeyPattern().IsMatch(key) ? key : throw new ArgumentException("invalid log key", nameof(key));

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,31}$")]
    private static partial Regex KeyPattern();
}
