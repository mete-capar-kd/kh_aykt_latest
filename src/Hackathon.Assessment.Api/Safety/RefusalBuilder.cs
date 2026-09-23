using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Telemetry;

namespace Hackathon.Assessment.Api.Safety;

public sealed class RefusalBuilder
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _templates;
    private readonly PromptCatalog _catalog;
    private readonly SafetyMetrics _metrics;

    public RefusalBuilder(PromptCatalog catalog, SafetyMetrics metrics)
    {
        _catalog = catalog;
        _metrics = metrics;
        _templates = Parse(catalog.RefusalTemplates);
    }

    public AskResponse Build(string reason, string language, string correlationId)
    {
        var selectedLanguage = language == "tr" ? "tr" : "en";
        if (!_templates[selectedLanguage].TryGetValue(reason, out var text))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown refusal reason.");
        }

        _metrics.Refusal(reason);
        if (reason == "content_filter")
        {
            _metrics.ContentFiltered();
        }

        return new AskResponse(
            text,
            AnswerType.Refusal,
            _catalog.PromptVersion,
            [],
            correlationId,
            null);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse(string content)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        string? language = null;
        foreach (var sourceLine in content.Split('\n'))
        {
            var line = sourceLine.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                language = line[3..];
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                result.Add(language, current);
                continue;
            }

            var separator = line.IndexOf(':');
            if (current is not null && separator > 0)
            {
                current.Add(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }
        }

        return result;
    }
}
