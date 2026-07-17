using System.Text.Json;
using System.Text.RegularExpressions;
using HonestDesk.Models;

namespace HonestDesk.Parsing;

public static partial class HelpRequestParser
{
    public static HelpRequest Parse(string uid, string messageId, string subject, string body)
    {
        var request = new HelpRequest
        {
            Uid = uid,
            MessageId = messageId,
            Subject = subject,
            CreatedAt = DateTimeOffset.Now
        };

        ParseSubject(request, subject);
        ParseBodyFields(request, body);
        ParseJsonBlock(request, body);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            request.Message = body.Trim();
        }

        return request;
    }

    private static void ParseSubject(HelpRequest request, string subject)
    {
        var parts = subject.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length >= 4 && parts[0].Equals("HF_HELP", StringComparison.OrdinalIgnoreCase))
        {
            request.ClientName = parts[1];
            request.InnMasked = parts[2];
            request.MachineName = parts[3];
        }
    }

    private static void ParseBodyFields(HelpRequest request, string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        var messageLines = new List<string>();

        foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var match = FieldRegex().Match(line);
            if (match.Success)
            {
                currentKey = match.Groups["key"].Value.Trim();
                fields[currentKey] = match.Groups["value"].Value.Trim();
                continue;
            }

            if (currentKey?.Equals("Message", StringComparison.OrdinalIgnoreCase) == true)
            {
                messageLines.Add(line);
            }
        }

        Assign(request, fields);
        if (messageLines.Count > 0)
        {
            request.Message = string.Join(Environment.NewLine, messageLines).Trim();
        }
    }

    private static void ParseJsonBlock(HelpRequest request, string body)
    {
        var match = JsonBlockRegex().Match(body);
        if (!match.Success)
        {
            return;
        }

        try
        {
            var json = match.Groups["json"].Value.Trim();
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (fields is null)
            {
                return;
            }

            var strings = fields.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            Assign(request, strings);
        }
        catch
        {
            // Human-readable fields remain enough for MVP.
        }
    }

    private static void Assign(HelpRequest request, IDictionary<string, string> fields)
    {
        SetIfPresent(fields, "ClientName", value => request.ClientName = value);
        SetIfPresent(fields, "InnMasked", value => request.InnMasked = value);
        SetIfPresent(fields, "Address", value => request.Address = value);
        SetIfPresent(fields, "ClientAddress", value => request.Address = value);
        SetIfPresent(fields, "FiscalAddress", value => request.Address = value);
        SetIfPresent(fields, "MachineName", value => request.MachineName = value);
        SetIfPresent(fields, "WindowsUser", value => request.WindowsUser = value);
        SetIfPresent(fields, "RuDesktopId", value => request.RuDesktopId = value);
        SetIfPresent(fields, "HonestFlowVersion", value => request.HonestFlowVersion = value);
        SetIfPresent(fields, "ProblemType", value => request.ProblemType = value);
        SetIfPresent(fields, "Message", value => request.Message = value);
        SetIfPresent(fields, "CreatedAt", value =>
        {
            if (DateTimeOffset.TryParse(value, out var createdAt))
            {
                request.CreatedAt = createdAt;
            }
        });
    }

    private static void SetIfPresent(IDictionary<string, string> fields, string key, Action<string> setter)
    {
        if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            setter(value.Trim());
        }
    }

    [GeneratedRegex(@"^(?<key>ClientName|InnMasked|Address|ClientAddress|FiscalAddress|MachineName|WindowsUser|RuDesktopId|HonestFlowVersion|ProblemType|Message|CreatedAt)\s*:\s*(?<value>.*)$")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"```json\s*(?<json>\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase)]
    private static partial Regex JsonBlockRegex();
}


