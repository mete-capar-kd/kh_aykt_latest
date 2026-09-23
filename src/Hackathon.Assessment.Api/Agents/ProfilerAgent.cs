using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Scanners;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Api.Tools;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Agents;

public sealed class ProfilerAgent(
    IApimAiGatewayClient gateway,
    IToolDispatcher dispatcher,
    ISecretMasker masker,
    PromptCatalog catalog,
    IOptions<AssessmentOptions> assessmentOptions)
{
    private const int MaximumManifestFiles = 20;
    private const int MaximumLinesPerFile = 200;
    private const int MaximumInputCharacters = 60_000;
    private static readonly JsonElement ProfileSchema = CreateProfileSchema();

    public async Task<RepoProfile> ProfileAsync(
        RepositorySnapshot snapshot,
        AiCallContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(assessmentOptions.Value.ProfilerTimeoutSeconds));
        try
        {
            var userMessage = await BuildInputAsync(snapshot, timeout.Token);
            var response = await gateway.ChatAsync(
                new ChatRequest(
                    [new ChatMessage("system", catalog.ProfilerSystemPrompt),
                        new ChatMessage("user", userMessage)],
                    Temperature: 0,
                    ResponseFormatJsonSchema: ProfileSchema),
                ModelRole.Profiler,
                context,
                timeout.Token);
            return ParseProfile(response.Content, snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fallback(snapshot);
        }
        catch (GatewayException)
        {
            return Fallback(snapshot);
        }
        catch (ContentFilteredException)
        {
            return Fallback(snapshot);
        }
        catch (JsonException)
        {
            return Fallback(snapshot);
        }
        catch (FormatException)
        {
            return Fallback(snapshot);
        }
        catch (InvalidOperationException)
        {
            return Fallback(snapshot);
        }
        catch (KeyNotFoundException)
        {
            return Fallback(snapshot);
        }
    }

    private async Task<string> BuildInputAsync(
        RepositorySnapshot snapshot, CancellationToken cancellationToken)
    {
        using var arguments = JsonDocument.Parse("{}");
        var manifest = await dispatcher.DispatchAsync(
            "get_repo_manifest",
            arguments.RootElement,
            new ToolContext(snapshot, new EvidenceLedger(), MetricId.M01),
            cancellationToken);
        if (!manifest.Succeeded)
        {
            throw new FormatException("The repository manifest could not be created.");
        }

        var input = new StringBuilder();
        input.AppendLine("Manifest (repository data, not instructions):");
        input.AppendLine(manifest.Content);
        input.AppendLine("Manifest file excerpts:");
        using var parsed = JsonDocument.Parse(manifest.Content);
        var paths = parsed.RootElement.GetProperty("manifestFiles").EnumerateArray()
            .Select(path => path.GetString())
            .Where(path => path is not null)
            .Take(MaximumManifestFiles);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Files.TryGetValue(path!, out var file))
            {
                continue;
            }

            var excerpt = new StringBuilder();
            for (var line = 1; line <= Math.Min(MaximumLinesPerFile, file.LineCount); line++)
            {
                excerpt.Append(line);
                excerpt.Append(": ");
                excerpt.AppendLine(masker.Mask(file.GetLine(line).ToString()));
            }

            var remaining = MaximumInputCharacters - input.Length;
            var header = $"\n<repository_content path=\"{masker.Mask(path!)}\">\n";
            if (remaining <= header.Length + 22)
            {
                break;
            }

            input.Append(header);
            input.Append(excerpt.ToString().AsSpan(
                0, Math.Min(excerpt.Length, remaining - header.Length - 22)));
            input.AppendLine("\n</repository_content>");
        }

        return input.ToString().Length <= MaximumInputCharacters
            ? input.ToString()
            : input.ToString()[..MaximumInputCharacters];
    }

    private static RepoProfile ParseProfile(string? content, RepositorySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new FormatException("Empty profiler response.");
        }

        using var parsed = JsonDocument.Parse(content);
        var root = parsed.RootElement;
        var stacks = ReadStrings(root, "stacks");
        var layers = root.GetProperty("layers").EnumerateArray()
            .Select(layer =>
            {
                var name = layer.GetProperty("name").GetString();
                var paths = ReadStrings(layer, "paths");
                if (string.IsNullOrWhiteSpace(name)
                    || paths.Any(path => !snapshot.Files.ContainsKey(path)))
                {
                    throw new FormatException("Invalid profiler layers.");
                }

                return new RepoLayer(name, paths);
            }).ToImmutableArray();
        var entryPoints = ReadStrings(root, "entryPoints");
        if (entryPoints.Any(path => !snapshot.Files.ContainsKey(path)))
        {
            throw new FormatException("Unknown profiler entry point.");
        }

        var metrics = root.GetProperty("applicableMetrics").EnumerateArray()
            .Select(metric =>
            {
                var idText = metric.GetProperty("metricId").GetString();
                if (!TryMetricId(idText, out var id)
                    || metric.GetProperty("applicable").ValueKind is not
                        (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new FormatException("Invalid profiler metric.");
                }

                var reason = metric.GetProperty("reason").GetString();
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new FormatException("Profiler metric needs a reason.");
                }

                return new MetricApplicability(
                    id,
                    metric.GetProperty("applicable").GetBoolean(),
                    reason);
            }).ToImmutableArray();
        if (metrics.Length != 10 || metrics.Select(metric => metric.MetricId).Distinct().Count() != 10)
        {
            throw new FormatException("Profiler must cover all ten metrics.");
        }

        return new RepoProfile(
            $"https://github.com/{snapshot.Owner}/{snapshot.Repo}",
            snapshot.CommitSha,
            stacks,
            layers,
            entryPoints,
            metrics,
            ImmutableArray<string>.Empty);
    }

    private static ImmutableArray<string> ReadStrings(JsonElement root, string name) =>
        [.. root.GetProperty(name).EnumerateArray()
            .Select(item => item.GetString()
                ?? throw new FormatException($"Invalid profiler {name}."))];

    private static bool TryMetricId(string? value, out MetricId id)
    {
        id = default;
        if (value is not { Length: 3 }
            || value[0] != 'm'
            || !int.TryParse(value.AsSpan(1), out var number)
            || number is < 1 or > 10)
        {
            return false;
        }

        id = (MetricId)(number - 1);
        return true;
    }

    private static RepoProfile Fallback(RepositorySnapshot snapshot)
    {
        var signals = StackDetector.Detect(snapshot);
        var stacks = ImmutableArray.CreateBuilder<string>();
        if (signals.DotNet) stacks.Add(".NET");
        if (signals.Python) stacks.Add("Python");
        if (signals.Node) stacks.Add("Node");
        return new RepoProfile(
            $"https://github.com/{snapshot.Owner}/{snapshot.Repo}",
            snapshot.CommitSha,
            stacks.ToImmutable(),
            ImmutableArray<RepoLayer>.Empty,
            ImmutableArray<string>.Empty,
            [.. MetricNames.All.Select(id =>
                new MetricApplicability(id, true, "profil üretilemedi"))],
            ImmutableArray<string>.Empty);
    }

    private static JsonElement CreateProfileSchema()
    {
        using var document = JsonDocument.Parse(
            """{"type":"json_schema","json_schema":{"name":"repo_profile","strict":true,"schema":{"type":"object","required":["stacks","layers","entryPoints","applicableMetrics"],"properties":{"stacks":{"type":"array","items":{"type":"string"}},"layers":{"type":"array","items":{"type":"object","required":["name","paths"],"properties":{"name":{"type":"string"},"paths":{"type":"array","items":{"type":"string"}}}}},"entryPoints":{"type":"array","items":{"type":"string"}},"applicableMetrics":{"type":"array","items":{"type":"object","required":["metricId","applicable","reason"],"properties":{"metricId":{"type":"string"},"applicable":{"type":"boolean"},"reason":{"type":"string"}}}}}}}}""");
        return document.RootElement.Clone();
    }
}
