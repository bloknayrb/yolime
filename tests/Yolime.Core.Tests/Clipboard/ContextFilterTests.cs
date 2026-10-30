using Yolime.Core.Clipboard;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Tests.Clipboard;

public class ContextFilterTests
{
    // Built at runtime so the literal secrets never appear in source as scanners would match them.
    private static string Key(string prefix, int n, char c = 'a') => prefix + new string(c, n);

    public static TheoryData<string> Secrets() => new()
    {
        Key("sk-", 40),
        Key("sk-ant-api03-", 60),
        Key("sk_live_", 24),
        Key("rk_live_", 24),
        Key("gsk_", 40),
        Key("ghp_", 36),
        Key("gho_", 36),
        Key("ghs_", 36),
        Key("ghu_", 36),
        Key("ghr_", 36),
        Key("github_pat_", 60),
        Key("AKIA", 16, 'Q'),
        Key("ASIA", 16, 'Q'),
        Key("AIza", 35),
        Key("xoxb-", 30, '1'),
        Key("xoxp-", 30, '1'),
        Key("glpat-", 20),
        Key("npm_", 36),
        Key("pypi-", 60),
        "eyJ" + new string('a', 20) + ".eyJ" + new string('b', 20) + "." + new string('c', 20),
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----",
        new string('f', 40),
        "password=hunter2",
        "api_key: abc123",
        "Authorization: Bearer " + new string('z', 30),
        "P@ssw0rd!xyz",
        "4111 1111 1111 1111",
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Selection_RedactsTheSecret_KeepsTheRest(string secret)
    {
        var f = ContextFilter.Apply($"before {secret} after", ContextSource.Selection, sensitiveMarkers: false)!;
        Assert.Equal(ContextVerdict.Redacted, f.Verdict);
        Assert.DoesNotContain(secret, f.Text);
        Assert.StartsWith("before", f.Text);
        Assert.EndsWith("after", f.Text);
        Assert.Contains("[redacted]", f.Text);
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Clipboard_WithASecret_IsRejected(string secret)
    {
        var f = ContextFilter.Apply($"before {secret} after", ContextSource.Clipboard, sensitiveMarkers: false)!;
        Assert.Equal(ContextVerdict.Rejected, f.Verdict);
        Assert.Equal("", f.Text);
    }

    [Theory]
    [InlineData("xoxo, see you soon")]
    [InlineData("See https://example.com/Docs/Page2?x=1 for details")]
    [InlineData("The id is 3f2504e0-4f89-11d3-9a0c-0305e82c3301 in prose")]
    [InlineData("Call getUserById(userId2) from UserService_v2.cs")]
    [InlineData("Email me at Sam.Lee99@example.org!")]
    [InlineData("Order 1234 5678 is ready")]
    [InlineData("Meet at 10:30, OK? Thanks!")]
    [InlineData("C:\\Users\\Sam\\Documents\\Plan2026.docx")]
    public void OrdinaryText_PassesUnchanged(string text)
    {
        var f = ContextFilter.Apply(text, ContextSource.Clipboard, sensitiveMarkers: false)!;
        Assert.Equal(ContextVerdict.Accepted, f.Verdict);
        Assert.Equal(text, f.Text);
    }

    [Fact]
    public void PasswordManagerMarkers_RejectTheClipboard()
    {
        var f = ContextFilter.Apply("plain words", ContextSource.Clipboard, sensitiveMarkers: true)!;
        Assert.Equal(ContextVerdict.Rejected, f.Verdict);
    }

    [Fact]
    public void InvisibleCharacters_AreStripped_AndTextIsNormalised()
    {
        // Bidi overrides, zero-width joiners and tag characters can smuggle instructions past a reader.
        var raw = "Hel\u200Blo \u202Eevil\u202C wor\uFEFFld \U000E0041\U000E0042 ﬁne";
        var f = ContextFilter.Apply(raw, ContextSource.Selection, sensitiveMarkers: false)!;
        Assert.Equal("Hello evil world  fine", f.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("\u200B\u200B")]
    public void EmptyContext_IsNull(string raw) => Assert.Null(ContextFilter.Apply(raw, ContextSource.Selection, false));

    [Fact]
    public void NewlinesAndTabs_Survive() =>
        Assert.Equal("a\nb\tc", ContextFilter.Apply("a\r\nb\tc", ContextSource.Selection, false)!.Text);
}
