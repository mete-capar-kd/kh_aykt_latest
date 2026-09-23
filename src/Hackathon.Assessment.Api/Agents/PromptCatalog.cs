using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Hackathon.Assessment.Api.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hackathon.Assessment.Api.Agents;

public sealed class PromptCatalog
{
    private static readonly string[] SeverityNames = ["critical", "high", "medium", "low", "info"];

    private readonly IReadOnlyDictionary<MetricId, MetricPrompt> _metrics;

    public PromptCatalog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Prompt catalog directory was not found: {root}");
        }

        var loadedFiles = new List<LoadedPromptFile>();
        ProfilerSystemPrompt = LoadText(root, "system/profiler.md", loadedFiles);
        EvaluatorSystemPrompt = LoadText(root, "system/evaluator.md", loadedFiles);
        SynthesizerSystemPrompt = LoadText(root, "system/synthesizer.md", loadedFiles);
        foreach (var systemFile in Directory.EnumerateFiles(
            Path.Combine(root, "system"), "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(systemFile);
            if (name is "profiler.md" or "evaluator.md" or "synthesizer.md")
            {
                continue;
            }

            LoadText(root, $"system/{name}", loadedFiles);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        var metrics = new Dictionary<MetricId, MetricPrompt>();

        foreach (var metricId in MetricNames.All)
        {
            var id = ToMetricId(metricId);
            var prompt = LoadText(root, $"metrics/{id}.md", loadedFiles);
            var rubricText = LoadText(root, $"rubrics/{id}.yaml", loadedFiles);
            var rubric = ParseRubric(deserializer, rubricText, metricId, $"rubrics/{id}.yaml");
            metrics.Add(metricId, new MetricPrompt(metricId, prompt, rubric));
        }

        _metrics = new ReadOnlyDictionary<MetricId, MetricPrompt>(metrics);
        PromptVersion = CalculateVersion(loadedFiles);
    }

    public string PromptVersion { get; }

    public string EvaluatorSystemPrompt { get; }

    public string ProfilerSystemPrompt { get; }

    public string SynthesizerSystemPrompt { get; }

    public MetricPrompt GetMetric(MetricId id)
    {
        if (!_metrics.TryGetValue(id, out var metric))
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown metric id.");
        }

        return metric;
    }

    private static string LoadText(
        string root,
        string relativePath,
        ICollection<LoadedPromptFile> loadedFiles)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Required prompt catalog file was not found: {relativePath}",
                fullPath);
        }

        var content = File.ReadAllText(fullPath);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException($"Prompt catalog file is empty: {relativePath}");
        }

        loadedFiles.Add(new LoadedPromptFile(relativePath, content));
        return content;
    }

    private static Rubric ParseRubric(
        IDeserializer deserializer,
        string yaml,
        MetricId expectedMetricId,
        string relativePath)
    {
        RubricDocument document;
        try
        {
            document = deserializer.Deserialize<RubricDocument>(yaml)
                ?? throw new InvalidDataException($"Rubric is empty: {relativePath}");
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new InvalidDataException($"Rubric YAML is invalid: {relativePath}", exception);
        }

        var expectedId = ToMetricId(expectedMetricId);
        Require(document.MetricId == expectedId, relativePath, $"metricId must be '{expectedId}'");
        Require(
            document.MetricName == MetricNames.GetName(expectedMetricId),
            relativePath,
            "metricName does not match the K2 metric name");
        Require(
            document.SourceRef == $"UseCase §6.{(int)expectedMetricId + 1}",
            relativePath,
            "sourceRef does not match the metric");
        Require(document.SubChecks is { Count: > 0 }, relativePath, "subChecks is required");
        Require(
            document.AcceptanceCriteria is { Count: > 0 },
            relativePath,
            "acceptanceCriteria is required");
        Require(document.SeverityGuide is not null, relativePath, "severityGuide is required");

        var subChecks = new List<RubricSubCheck>(document.SubChecks!.Count);
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.SubChecks.Count; index++)
        {
            var source = document.SubChecks[index];
            var expectedSubCheckId = $"{expectedId}-sc{index + 1:00}";
            Require(source.Id == expectedSubCheckId, relativePath, $"subCheck id must be '{expectedSubCheckId}'");
            Require(!string.IsNullOrWhiteSpace(source.Title), relativePath, $"{expectedSubCheckId} title is required");
            Require(source.CodeVerifiable.HasValue, relativePath, $"{expectedSubCheckId} codeVerifiable is required");
            Require(
                seenTitles.Add(source.Title!),
                relativePath,
                $"{expectedSubCheckId} title must be unique");
            subChecks.Add(new RubricSubCheck(
                source.Id!,
                source.Title!,
                source.CodeVerifiable.GetValueOrDefault()));
        }

        var acceptanceCriteria = new List<string>(document.AcceptanceCriteria!.Count);
        foreach (var criterion in document.AcceptanceCriteria)
        {
            Require(!string.IsNullOrWhiteSpace(criterion), relativePath, "acceptance criterion cannot be empty");
            Require(
                !acceptanceCriteria.Contains(criterion!, StringComparer.Ordinal),
                relativePath,
                "acceptance criteria must be unique");
            acceptanceCriteria.Add(criterion!);
        }

        Require(
            document.SeverityGuide!.Count == SeverityNames.Length
            && SeverityNames.All(document.SeverityGuide.ContainsKey),
            relativePath,
            "severityGuide must contain exactly critical, high, medium, low and info");
        foreach (var entry in document.SeverityGuide)
        {
            Require(!string.IsNullOrWhiteSpace(entry.Value), relativePath, $"severityGuide.{entry.Key} is required");
        }

        return new Rubric(
            document.MetricId!,
            document.MetricName!,
            document.SourceRef!,
            subChecks.AsReadOnly(),
            acceptanceCriteria.AsReadOnly(),
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(document.SeverityGuide, StringComparer.Ordinal)));
    }

    private static void Require(bool condition, string relativePath, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException($"Invalid rubric '{relativePath}': {message}.");
        }
    }

    private static string CalculateVersion(IEnumerable<LoadedPromptFile> loadedFiles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in loadedFiles.OrderBy(static file => file.RelativePath, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData("\n"u8);
            hash.AppendData(Encoding.UTF8.GetBytes(file.Content));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }

    private static string ToMetricId(MetricId id) => id.ToString().ToLowerInvariant();

    private sealed record LoadedPromptFile(string RelativePath, string Content);

    private sealed class RubricDocument
    {
        public string? MetricId { get; set; }

        public string? MetricName { get; set; }

        public string? SourceRef { get; set; }

        public List<RubricSubCheckDocument>? SubChecks { get; set; }

        public List<string?>? AcceptanceCriteria { get; set; }

        public Dictionary<string, string>? SeverityGuide { get; set; }
    }

    private sealed class RubricSubCheckDocument
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public bool? CodeVerifiable { get; set; }
    }
}

public sealed record MetricPrompt(MetricId MetricId, string Prompt, Rubric Rubric);

public sealed record Rubric(
    string Id,
    string Name,
    string SourceRef,
    IReadOnlyList<RubricSubCheck> SubChecks,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyDictionary<string, string> SeverityGuide);

public sealed record RubricSubCheck(string Id, string Title, bool CodeVerifiable);
