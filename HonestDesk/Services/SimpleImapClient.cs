using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HonestDesk.Services;

public sealed partial class SimpleImapClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _ssl;
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private int _tagNumber;

    public SimpleImapClient(string host, int port, bool ssl)
    {
        _host = host;
        _port = port;
        _ssl = ssl;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_host, _port, cancellationToken);
        _stream = _tcpClient.GetStream();

        if (_ssl)
        {
            var sslStream = new SslStream(_stream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsClientAsync(_host);
            _stream = sslStream;
        }

        await ReadResponseAsync("*", cancellationToken);
    }

    public Task LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        ExecuteOkAsync($"LOGIN {Quote(email)} {Quote(password)}", cancellationToken);

    /// <summary>
    /// Forces the IMAP server to synchronize mailbox state.
    /// This is especially useful with Yandex IMAP: a freshly delivered message can appear only
    /// after an extra synchronization command, otherwise the next poll may see stale SEARCH data.
    /// </summary>
    public Task NoOpAsync(CancellationToken cancellationToken) =>
        ExecuteOkAsync("NOOP", cancellationToken);

    public Task SelectFolderAsync(string folder, CancellationToken cancellationToken) =>
        ExecuteOkAsync($"SELECT {Quote(folder)}", cancellationToken);

    public async Task<IReadOnlyList<string>> SearchAllUidsAsync(CancellationToken cancellationToken)
    {
        var response = await ExecuteOkAsync("UID SEARCH ALL", cancellationToken);
        return ParseSearchUids(response);
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken)
    {
        var response = await ExecuteOkAsync("LIST \"\" \"*\"", cancellationToken);
        var folders = response.Split('\n')
            .Select(ParseListFolderName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return folders;
    }


    public async Task<RawMailMessage> FetchHeadersAsync(string uid, CancellationToken cancellationToken)
    {
        var response = await ExecuteOkAsync($"UID FETCH {uid} (BODY.PEEK[HEADER.FIELDS (SUBJECT MESSAGE-ID)])", cancellationToken);
        var subject = DecodeHeader(HeaderRegex("Subject").Match(response).Groups["value"].Value.Trim());
        var messageId = HeaderRegex("Message-ID").Match(response).Groups["value"].Value.Trim();
        return new RawMailMessage(subject, messageId, "");
    }

    public async Task<RawMailMessage> FetchMessageAsync(string uid, CancellationToken cancellationToken)
    {
        var response = await ExecuteOkAsync($"UID FETCH {uid} (BODY.PEEK[HEADER.FIELDS (SUBJECT MESSAGE-ID)] BODY.PEEK[TEXT])", cancellationToken);
        var subject = DecodeHeader(HeaderRegex("Subject").Match(response).Groups["value"].Value.Trim());
        var messageId = HeaderRegex("Message-ID").Match(response).Groups["value"].Value.Trim();
        var body = MailBodyDecoder.Decode(ExtractBody(response));
        return new RawMailMessage(subject, messageId, body);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteOkAsync("LOGOUT", cancellationToken);
        }
        catch
        {
            // Closing the socket is enough for shutdown.
        }
    }

    private async Task<string> ExecuteOkAsync(string command, CancellationToken cancellationToken)
    {
        var tag = "A" + Interlocked.Increment(ref _tagNumber).ToString("000");
        await WriteLineAsync($"{tag} {command}", cancellationToken);
        var response = await ReadResponseAsync(tag, cancellationToken);
        var taggedLine = response.Split('\n').FirstOrDefault(x => x.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!taggedLine.Contains(" OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(CleanImapResponse(taggedLine.Length == 0 ? response : taggedLine));
        }

        return response;
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("IMAP connection is not open.");
        }

        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task<string> ReadResponseAsync(string tag, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("IMAP connection is not open.");
        }

        var builder = new StringBuilder();
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            builder.AppendLine(line);

            var literal = LiteralRegex().Match(line);
            if (literal.Success && int.TryParse(literal.Groups["size"].Value, out var size) && size > 0)
            {
                var bytes = await ReadExactAsync(size, cancellationToken);
                builder.AppendLine($"__HONESTDESK_LITERAL_START:{size}__");
                builder.Append(Encoding.UTF8.GetString(bytes));
                builder.AppendLine();
                builder.AppendLine("__HONESTDESK_LITERAL_END__");
            }

            if (tag == "*" && line.StartsWith("* ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return builder.ToString();
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            var read = await _stream!.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new IOException("IMAP connection closed.");
            }

            bytes.Add(buffer[0]);
            if (buffer[0] == '\n')
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r', '\n');
    }

    private async Task<byte[]> ReadExactAsync(int size, CancellationToken cancellationToken)
    {
        var bytes = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var read = await _stream!.ReadAsync(bytes.AsMemory(offset, size - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("IMAP connection closed while reading message body.");
            }

            offset += read;
        }

        return bytes;
    }

    private static string ParseListFolderName(string line)
    {
        if (!line.StartsWith("* LIST", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var flagsEnd = line.IndexOf(") ", StringComparison.Ordinal);
        if (flagsEnd < 0)
        {
            return "";
        }

        var value = line[(flagsEnd + 2)..].Trim();
        var delimiter = ReadListAtom(ref value);
        if (string.IsNullOrWhiteSpace(delimiter))
        {
            return "";
        }

        return ReadListAtom(ref value);
    }

    private static string ReadListAtom(ref string value)
    {
        value = value.TrimStart();
        if (value.Length == 0)
        {
            return "";
        }

        if (value[0] != '"')
        {
            var space = value.IndexOf(' ');
            var atom = space < 0 ? value : value[..space];
            value = space < 0 ? "" : value[(space + 1)..];
            return atom.Trim();
        }

        var builder = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                builder.Append(value[++i]);
                continue;
            }

            if (value[i] == '"')
            {
                value = value[(i + 1)..];
                return builder.ToString();
            }

            builder.Append(value[i]);
        }

        value = "";
        return builder.ToString();
    }

    private static IReadOnlyList<string> ParseSearchUids(string response)
    {
        var line = response.Split('\n').FirstOrDefault(x => x.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return [];
        }

        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(2)
            .Select(x => x.Trim())
            .Where(x => x.All(char.IsDigit))
            .ToList();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string ExtractBody(string response)
    {
        var marker = "BODY[TEXT]";
        var index = response.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var afterMarker = response[(index + marker.Length)..];
        var start = LiteralStartRegex().Match(afterMarker);
        if (start.Success)
        {
            var bodyStart = start.Index + start.Length;
            var end = afterMarker.IndexOf("__HONESTDESK_LITERAL_END__", bodyStart, StringComparison.Ordinal);
            if (end >= 0)
            {
                return afterMarker[bodyStart..end].Trim('\r', '\n');
            }
        }

        return "";
    }

    private static string DecodeHeader(string value)
    {
        return EncodedWordRegex().Replace(value, match =>
        {
            try
            {
                var charset = match.Groups["charset"].Value;
                var encoding = match.Groups["encoding"].Value.ToUpperInvariant();
                var data = match.Groups["data"].Value;
                var bytes = encoding == "B"
                    ? Convert.FromBase64String(data)
                    : DecodeQuotedPrintableHeader(data);
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch
            {
                return match.Value;
            }
        });
    }

    private static byte[] DecodeQuotedPrintableHeader(string data)
    {
        data = data.Replace('_', ' ');
        var bytes = new List<byte>();
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == '=' && i + 2 < data.Length && byte.TryParse(data.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                bytes.Add(value);
                i += 2;
            }
            else
            {
                bytes.Add((byte)data[i]);
            }
        }

        return bytes.ToArray();
    }

    private static Regex HeaderRegex(string name) => new($@"^{Regex.Escape(name)}:\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string CleanImapResponse(string response) => response.Replace("\r", " ").Replace("\n", " ").Trim();

    public void Dispose()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }

    [GeneratedRegex(@"\{(?<size>\d+)\}$")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"__HONESTDESK_LITERAL_START:(?<size>\d+)__\r?\n")]
    private static partial Regex LiteralStartRegex();

    [GeneratedRegex(@"=\?(?<charset>[^?]+)\?(?<encoding>[BQbq])\?(?<data>[^?]+)\?=")]
    private static partial Regex EncodedWordRegex();
}

public sealed record RawMailMessage(string Subject, string MessageId, string Body);

