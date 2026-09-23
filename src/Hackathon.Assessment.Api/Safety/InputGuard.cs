using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Telemetry;

namespace Hackathon.Assessment.Api.Safety;

public sealed record InputGuardResult(string NormalizedQuestion, bool SuspectedInjection);

public interface IInputGuard
{
    InputGuardResult Process(string question);
}

public sealed class InputGuard : IInputGuard
{
    private readonly string[] _literalPatterns;
    private readonly Regex[] _regexPatterns;
    private readonly SafetyMetrics? _safetyMetrics;

    public InputGuard(SafetyMetrics? safetyMetrics = null)
    {
        _safetyMetrics = safetyMetrics;
        var patternPath = Path.Combine(
            AppContext.BaseDirectory, "prompts", "guard", "injection-patterns.txt");
        var lines = File.ReadAllLines(patternPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        var patterns = lines
            .Where(line => line.StartsWith("re:", StringComparison.Ordinal))
            .Select(line => new Regex(
                line[3..],
                RegexOptions.CultureInvariant
                    | RegexOptions.IgnoreCase
                    | RegexOptions.NonBacktracking))
            .ToArray();

        _literalPatterns = lines
            .Where(line => !line.StartsWith("re:", StringComparison.Ordinal))
            .Select(line => line.ToLower(CultureInfo.GetCultureInfo("tr-TR")))
            .ToArray();
        _regexPatterns = patterns;
    }

    public InputGuardResult Process(string question)
    {
        var filtered = new StringBuilder(question.Length);
        foreach (var character in question)
        {
            if (character is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF')
            {
                continue;
            }

            if (char.IsControl(character)
                && character is not ('\t' or '\n' or '\r'))
            {
                continue;
            }

            filtered.Append(character);
        }

        var normalized = filtered
            .ToString()
            .Normalize(NormalizationForm.FormKC)
            .Trim();
        var lowerQuestion = normalized.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var suspectedInjection = _literalPatterns.Any(pattern =>
                lowerQuestion.Contains(pattern, StringComparison.Ordinal))
            || _regexPatterns.Any(pattern => pattern.IsMatch(lowerQuestion));
        if (suspectedInjection)
        {
            _safetyMetrics?.InjectionSuspected();
        }

        return new InputGuardResult(normalized, suspectedInjection);
    }
}
