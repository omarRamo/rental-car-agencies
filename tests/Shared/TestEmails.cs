using System.Text;
using System.Text.RegularExpressions;

namespace CarRental.Testing;

/// <summary>An e-mail written by the development transport (<c>.eml</c> file): recipient, decoded subject and bodies.</summary>
public sealed record TestEmail(string File, string To, string Subject, string Text, string Html);

/// <summary>
/// Reads the <c>.eml</c> files of a test host defensively (a file can be in the middle of its rename, or unreadable for a
/// moment on Windows): an unreadable file is skipped and read again on the next poll; waiting is a condition, never a
/// fixed delay.
/// </summary>
public static partial class TestEmails
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Every e-mail of the folder that can be read now.</summary>
    public static IReadOnlyList<TestEmail> ReadAll(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var emails = new List<TestEmail>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.eml"))
        {
            if (TryRead(file) is { } email)
            {
                emails.Add(email);
            }
        }

        return emails;
    }

    /// <summary>The first e-mail matching <paramref name="predicate"/>, waiting for it up to <paramref name="timeout"/>.</summary>
    public static async Task<TestEmail> WaitForAsync(string directory, Func<TestEmail, bool> predicate, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            if (ReadAll(directory).FirstOrDefault(predicate) is { } email)
            {
                return email;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No e-mail matched in {directory} before the deadline.");
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>The absolute links of an e-mail text.</summary>
    public static IReadOnlyList<string> Links(TestEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return [.. Link().Matches(email.Text).Select(m => m.Value)];
    }

    /// <summary>The six-digit code of an access e-mail (the line "Votre code : 123456" / "Your code: 123456").</summary>
    public static string? Code(TestEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return SixDigits().Match(email.Text) is { Success: true } match ? match.Groups["code"].Value : null;
    }

    private static TestEmail? TryRead(string file)
    {
        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var headers = content.Split("\r\n\r\n", 2)[0];
        var to = Header(headers, "To") ?? string.Empty;
        var subject = Decode(Header(headers, "Subject") ?? string.Empty);
        return new TestEmail(file, to, subject, Part(content, "text/plain"), Part(content, "text/html"));
    }

    private static string? Header(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith(name + ": ", StringComparison.Ordinal))
            {
                return line[(name.Length + 2)..];
            }
        }

        return null;
    }

    /// <summary>An RFC 2047 encoded word (<c>=?utf-8?B?…?=</c>), as the transport writes them.</summary>
    private static string Decode(string value) =>
        EncodedWord().Replace(value, match => Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["text"].Value)));

    private static string Part(string content, string mediaType)
    {
        var marker = $"Content-Type: {mediaType}; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n";
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = content.IndexOf("\r\n\r\n", start, StringComparison.Ordinal);
        var base64 = (end < 0 ? content[start..] : content[start..end]).Replace("\r\n", string.Empty, StringComparison.Ordinal);
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"=\?utf-8\?B\?(?<text>[A-Za-z0-9+/=]+)\?=")]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex Link();

    [GeneratedRegex(@"code ?: (?<code>[0-9]{6})", RegexOptions.IgnoreCase)]
    private static partial Regex SixDigits();
}
