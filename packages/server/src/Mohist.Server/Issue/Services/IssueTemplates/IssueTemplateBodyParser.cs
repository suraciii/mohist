using Mohist.Server.Issue.Domain.IssueTemplate;

namespace Mohist.Server.Issue.Services.IssueTemplates;

internal static class IssueTemplateBodyParser
{
    internal static IReadOnlyList<IssueTemplateSection> Parse(string body)
    {
        var sections = new List<IssueTemplateSection>();
        var lines = body.Replace("\r", string.Empty).Split('\n');
        var currentTitle = (string?)null;
        var currentBodyLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ") && !line.StartsWith("### "))
            {
                if (currentTitle is not null)
                {
                    sections.Add(BuildSection(currentTitle, currentBodyLines));
                }
                currentTitle = line[3..].Trim();
                currentBodyLines.Clear();
            }
            else
            {
                currentBodyLines.Add(line);
            }
        }

        if (currentTitle is not null)
        {
            sections.Add(BuildSection(currentTitle, currentBodyLines));
        }

        return sections;
    }

    private static IssueTemplateSection BuildSection(string title, List<string> bodyLines)
    {
        var body = string.Join("\n", bodyLines).Trim();
        var guidance = string.Empty;
        var placeholder = body;

        if (body.StartsWith("<!--", StringComparison.Ordinal))
        {
            var closeIndex = body.IndexOf("-->", StringComparison.Ordinal);
            if (closeIndex >= 0)
            {
                guidance = body[4..closeIndex].Trim();
                placeholder = body[(closeIndex + 3)..].Trim();
            }
        }

        return new IssueTemplateSection(title, guidance, placeholder);
    }
}
