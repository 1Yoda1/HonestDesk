using System.Text;
using System.Text.RegularExpressions;

namespace HonestDesk.Services;

public static partial class MailBodyDecoder
{
    public static string Decode(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return "";
        }

        var plainPart = ExtractTextPlainPart(rawBody);
        return DecodeSinglePart(plainPart ?? rawBody).Trim();
    }

    private static string? ExtractTextPlainPart(string body)
    {
        var parts = body.Split("--", StringSplitOptions.None);
        foreach (var part in parts)
        {
            if (part.Contains("Content-Type: text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return part;
            }
        }

        return null;
    }

    private static string DecodeSinglePart(string part)
    {
        var headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (headerEnd < 0)
        {
            headerEnd = part.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        var headers = headerEnd >= 0 ? part[..headerEnd] : "";
        var payload = headerEnd >= 0 ? part[(headerEnd + separatorLength)..] : part;
        var charset = CharsetRegex().Match(headers).Groups["charset"].Value;
        var transferEncoding = TransferEncodingRegex().Match(headers).Groups["encoding"].Value;
        var encoding = GetEncoding(charset);

        try
        {
            if (transferEncoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeBase64(payload, encoding) ?? payload;
            }

            if (transferEncoding.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase))
            {
                return encoding.GetString(DecodeQuotedPrintable(payload));
            }

            var implicitBase64 = DecodeBase64(payload, Encoding.UTF8);
            if (implicitBase64 is not null && LooksLikeHelpRequest(implicitBase64))
            {
                return implicitBase64;
            }
        }
        catch
        {
            return payload;
        }

        return payload;
    }

    private static string? DecodeBase64(string value, Encoding encoding)
    {
        var compact = Regex.Replace(value.Trim(), @"\s+", "");
        if (compact.Length < 16 || compact.Length % 4 != 0 || !Base64Regex().IsMatch(compact))
        {
            return null;
        }

        try
        {
            return encoding.GetString(Convert.FromBase64String(compact));
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeHelpRequest(string value)
    {
        return value.Contains("ClientName:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("RuDesktopId:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("\"ClientName\"", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] DecodeQuotedPrintable(string value)
    {
        value = value.Replace("=\r\n", "").Replace("=\n", "");
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && byte.TryParse(value.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var decoded))
            {
                bytes.Add(decoded);
                i += 2;
            }
            else
            {
                bytes.Add((byte)value[i]);
            }
        }

        return bytes.ToArray();
    }

    private static Encoding GetEncoding(string charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    [GeneratedRegex("charset=(?<charset>[^;\\r\\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    [GeneratedRegex("Content-Transfer-Encoding:\\s*(?<encoding>[^\\r\\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TransferEncodingRegex();

    [GeneratedRegex("^[A-Za-z0-9+/]*={0,2}$")]
    private static partial Regex Base64Regex();
}
