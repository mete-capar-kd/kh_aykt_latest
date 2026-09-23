using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Tools;

public sealed class GlobMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _cache = new(StringComparer.Ordinal);

    public Regex Compile(string glob)
    {
        if (glob.Length is < 1 or > 256)
        {
            throw new ArgumentException("glob must contain 1 to 256 characters.", nameof(glob));
        }

        return _cache.GetOrAdd(glob, static pattern =>
        {
            var builder = new StringBuilder("^");
            for (var index = 0; index < pattern.Length; index++)
            {
                if (pattern[index] == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        builder.Length -= 2;
                        builder.Append("(?:.*/)?");
                        index++;
                    }
                }
                else if (pattern[index] == '*')
                {
                    builder.Append("[^/]*");
                }
                else if (pattern[index] == '?')
                {
                    builder.Append("[^/]");
                }
                else
                {
                    builder.Append(Regex.Escape(pattern[index].ToString()));
                }
            }

            return new Regex(builder.Append('$').ToString(),
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromSeconds(2));
        });
    }
}

internal static class ToolArguments
{
    public static bool TryString(JsonElement args, string key, out string value)
    {
        value = "";
        if (!args.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return true;
    }

    public static bool TryOptionalInt(
        JsonElement args, string key, int fallback, out int value)
    {
        value = fallback;
        if (!args.TryGetProperty(key, out var element))
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    public static bool TryOptionalBool(
        JsonElement args, string key, bool fallback, out bool value)
    {
        value = fallback;
        if (!args.TryGetProperty(key, out var element))
        {
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    public static string MaskedPath(ISecretMasker masker, string path) => masker.Mask(path);

    public static int ByteCount(SnapshotFile file) =>
        Encoding.UTF8.GetByteCount(file.Content);
}

public sealed class GetRepoManifestTool(ISecretMasker masker) : IReadOnlyRepositoryTool
{
    public string Name => "get_repo_manifest";

    public ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = context.Snapshot.Files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var manifestFiles = files.Where(file =>
            file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith("requirements.txt", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file.Path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file.Path).StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || (file.Path.StartsWith(".github/workflows/", StringComparison.Ordinal)
                && (file.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || file.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            || (Path.GetFileName(file.Path).StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase)
                && file.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            .Select(file => ToolArguments.MaskedPath(masker, file.Path)).ToArray();
        var languageGroups = files.GroupBy(file => Path.GetExtension(file.Path),
            StringComparer.OrdinalIgnoreCase);
        return ToolResult.From(new
        {
            paths = files.Take(500).Select(file => ToolArguments.MaskedPath(masker, file.Path)),
            languages = languageGroups.Select(group => new
            {
                extension = masker.Mask(group.Key),
                files = group.Count(),
                lines = group.Sum(file => file.LineCount)
            }),
            manifestFiles,
            truncated = files.Length > 500,
            total = files.Length
        });
    }
}

public sealed class ListFilesTool(GlobMatcher globs, ISecretMasker masker) : IReadOnlyRepositoryTool
{
    public string Name => "list_files";

    public ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryString(arguments, "glob", out var pattern)
            || !ToolArguments.TryOptionalInt(arguments, "limit", 200, out var limit)
            || limit is < 1 or > 200)
        {
            return ToolResult.Error("Invalid glob or limit; limit must be between 1 and 200.");
        }

        try
        {
            var glob = globs.Compile(pattern);
            var results = context.Snapshot.Files.Values.Where(file =>
                glob.IsMatch(file.Path)).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return ToolResult.From(new
            {
                files = results.Take(limit).Select(file => new
                {
                    path = ToolArguments.MaskedPath(masker, file.Path),
                    size = ToolArguments.ByteCount(file),
                    lines = file.LineCount
                }),
                truncated = results.Length > limit,
                total = results.Length
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return ToolResult.Error("Glob matching exceeded its time limit.");
        }
        catch (ArgumentException)
        {
            return ToolResult.Error("Invalid glob.");
        }
    }
}

public sealed class SearchCodeTool(
    GlobMatcher globs, ISecretMasker masker, TimeProvider timeProvider) : IReadOnlyRepositoryTool
{
    public string Name => "search_code";

    public ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryString(arguments, "pattern", out var pattern)
            || pattern.Length is < 1 or > 2048
            || !ToolArguments.TryOptionalBool(arguments, "regex", true, out var useRegex)
            || !ToolArguments.TryOptionalInt(arguments, "max_results", 200, out var limit)
            || limit is < 1 or > 200)
        {
            return ToolResult.Error("Invalid search pattern, regex flag or max_results.");
        }

        Regex? glob = null;
        var matches = new List<object>(limit);
        var total = 0;
        var truncated = false;
        try
        {
            if (arguments.TryGetProperty("glob", out _))
            {
                if (!ToolArguments.TryString(arguments, "glob", out var globPattern))
                {
                    return ToolResult.Error("Invalid glob.");
                }

                glob = globs.Compile(globPattern);
            }

            var regex = useRegex
                ? new Regex(pattern,
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2))
                : null;
            var started = timeProvider.GetTimestamp();
            foreach (var file in context.Snapshot.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                if (glob is not null && !glob.IsMatch(file.Path))
                {
                    continue;
                }

                for (var number = 1; number <= file.LineCount; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeProvider.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
                    {
                        truncated = true;
                        return Result();
                    }

                    var line = file.GetLine(number);
                    if (regex is null
                            ? line.IndexOf(pattern.AsSpan(), StringComparison.Ordinal) < 0
                            : !regex.IsMatch(line))
                    {
                        continue;
                    }

                    total++;
                    if (matches.Count == limit)
                    {
                        truncated = true;
                        return Result();
                    }

                    var text = masker.Mask(line.ToString());
                    matches.Add(new
                    {
                        path = ToolArguments.MaskedPath(masker, file.Path),
                        line = number,
                        text = text.Length > 300 ? text[..300] : text
                    });
                    context.EvidenceLedger.MarkSeen(file.Path, number);
                    if (text.Length > 300)
                    {
                        truncated = true;
                    }
                }
            }

            return Result();

            ToolResult Result() => ToolResult.From(new { results = matches, truncated, total });
        }
        catch (RegexMatchTimeoutException)
        {
            return ToolResult.From(new { results = matches, truncated = true, total });
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return ToolResult.Error("Invalid or unsupported search regex/glob.");
        }
    }
}

public sealed class ReadFileTool(ISecretMasker masker) : IReadOnlyRepositoryTool
{
    public string Name => "read_file";

    public ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryString(arguments, "path", out var path)
            || !context.Snapshot.Files.TryGetValue(path, out var file)
            || !ToolArguments.TryOptionalInt(arguments, "start_line", 1, out var start)
            || !ToolArguments.TryOptionalInt(arguments, "end_line", file.LineCount, out var end)
            || start < 1 || end < start || end > file.LineCount)
        {
            return ToolResult.Error("Invalid file path or one-based line range.");
        }

        var last = Math.Min(end, start + 1499);
        var lines = new List<string>(last - start + 1);
        for (var number = start; number <= last; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add($"{number}: {masker.Mask(file.GetLine(number).ToString())}");
            context.EvidenceLedger.MarkSeen(path, number);
        }

        return ToolResult.From(new
        {
            path = ToolArguments.MaskedPath(masker, path),
            lines,
            truncated = last < end,
            total = file.LineCount
        });
    }
}

public sealed class RunScannerTool(IScannerRunner runner, ISecretMasker masker) : IReadOnlyRepositoryTool
{
    public string Name => "run_scanner";

    public ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryString(arguments, "metric_id", out var id)
            || id.Length != 3
            || id[0] != 'm'
            || !int.TryParse(id.AsSpan(1), out var metricNumber)
            || metricNumber is < 1 or > 10)
        {
            return ToolResult.Error("Invalid metric_id; expected m01 through m10.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidates = runner.Run((MetricId)(metricNumber - 1), context.Snapshot);
        return ToolResult.From(new
        {
            candidates = candidates.Select(candidate => new
            {
                ruleId = masker.Mask(candidate.RuleId),
                file = masker.Mask(candidate.File),
                candidate.StartLine,
                candidate.EndLine,
                context = masker.Mask(candidate.MaskedContext),
                role = masker.Mask(candidate.FileRole)
            }),
            truncated = false,
            total = candidates.Length
        });
    }
}

public sealed class RecordFindingTool(RecordFindingValidator validator) : IReadOnlyRepositoryTool
{
    public string Name => "record_finding";

    public ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!arguments.TryGetProperty("finding", out var json)
            || json.ValueKind != JsonValueKind.Object
            || !ToolArguments.TryString(json, "title", out var title)
            || !ToolArguments.TryString(json, "severity", out var severity)
            || !ToolArguments.TryString(json, "confidence", out var confidence)
            || !ToolArguments.TryString(json, "standardRef", out var standardRef)
            || !ToolArguments.TryString(json, "file", out var file)
            || !ToolArguments.TryString(json, "snippet", out var snippet)
            || !ToolArguments.TryString(json, "recommendation", out var recommendation)
            || !json.TryGetProperty("startLine", out _)
            || !json.TryGetProperty("endLine", out _)
            || !ToolArguments.TryOptionalInt(json, "startLine", 0, out var start)
            || !ToolArguments.TryOptionalInt(json, "endLine", 0, out var end))
        {
            return ToolResult.Error("Invalid finding arguments.");
        }

        var input = new FindingInput(
            ToolArguments.TryString(json, "id", out var id) ? id : "",
            title, severity, confidence, standardRef,
            ToolArguments.TryString(json, "rationale", out var rationale) ? rationale : "",
            ToolArguments.TryString(json, "impact", out var impact) ? impact : "",
            file, start, end, snippet, recommendation);
        return ToolResult.From(validator.Record(context, input));
    }
}
