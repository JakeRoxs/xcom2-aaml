using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AAML.Infrastructure.Common.Compatibility.Steam;

/// <summary>Parses Steam Workshop changelog HTML using the legacy markup assumptions.</summary>
public static partial class LegacySteamChangelogParser
{
    /// <summary>Converts matching changelog entries to the legacy Windows text format.</summary>
    public static string Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var output = new StringBuilder();
        foreach (Match match in ChangelogRegex().Matches(html))
        {
            var description = match.Groups[2].Value.Replace("<br>", "\r\n\t", StringComparison.Ordinal);
            description = WebUtility.HtmlDecode(description);
            description = HtmlRegex().Replace(description, string.Empty).Trim();
            if (description.Length == 0)
            {
                description = "No description.";
            }

            output.Append(match.Groups[1].Value.Trim()).Append("\r\n");
            output.Append('\t').Append(description).Append("\r\n\r\n");
        }

        return output.ToString();
    }

    [GeneratedRegex("<div class=\"detailBox workshopAnnouncement noFooter changeLogCtn\">\\s*<div class=\"changelog headline\">\\s*(.*?)\\s*</div>\\s*(?:<div style=\"clear: right\"></div>\\s*)?<p id=\"[0-9]+\">((?:.|\\n)*?)</p>")]
    private static partial Regex ChangelogRegex();

    [GeneratedRegex("<.*?>")]
    private static partial Regex HtmlRegex();
}
