using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Orchestration;

public static class QuestionClassifier
{
    private static readonly Regex TurkishWords = new(
        @"\b(?:mı|mi|mu|mü|nasıl|neden|hangi|nedir|kim|uygulama|sahibi|dosya|var mı)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TurkishYesNo = new(
        @"\b(?:mı|mi|mu|mü)\s*\?*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EnglishYesNo = new(
        @"^(?:is|are|does|do|can|has|have|should)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex FileName = new(
        @"[\w./-]+\.(?:csproj|cs|json|ya?ml|md|py|js|tsx?|java|go|bicep|tf|sql|xml|config|txt|dockerfile)\b|\bDockerfile\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex OwnerQuestion = new(
        @"(?:\bowner\b|\bsahib[ie]\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string Language(string question) =>
        question.IndexOfAny("çğıöşüİ".AsSpan()) >= 0 || TurkishWords.IsMatch(question)
            ? "tr"
            : "en";

    public static string QuestionType(string question, string language) =>
        (language == "tr" ? TurkishYesNo : EnglishYesNo).IsMatch(question.Trim())
            ? "yes_no"
            : "open";

    public static string? MissingFile(string question, RepositorySnapshot snapshot)
    {
        foreach (Match match in FileName.Matches(question))
        {
            var referenced = match.Value;
            if (snapshot.Files.Keys.Any(path =>
                path.Equals(referenced, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/" + referenced, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return referenced;
        }

        return null;
    }

    public static bool IsOwnerQuestion(string question) =>
        OwnerQuestion.IsMatch(question);
}
