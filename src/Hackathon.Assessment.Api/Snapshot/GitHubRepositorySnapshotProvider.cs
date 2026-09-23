using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Options;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Snapshot;

public sealed class GitHubRepositorySnapshotProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<RepositoryOptions> repositoryOptions,
    IOptions<AssessmentOptions> assessmentOptions) : IRepositorySnapshotProvider
{
    private const string ApiHost = "api.github.com";
    private const long MaximumRawTarBytes = 200L * 1024 * 1024;
    private static readonly Regex OwnerPattern = new(
        "^[A-Za-z0-9-]{1,39}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RepoPattern = new(
        "^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RefPattern = new(
        "^[A-Za-z0-9._/-]{1,200}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ShaPattern = new(
        "^[0-9a-f]{40}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("github");
    private readonly RepositoryOptions _repositoryOptions = repositoryOptions.Value;
    private readonly AssessmentOptions _assessmentOptions = assessmentOptions.Value;

    public async Task<RepositorySnapshot> GetAsync(
        Uri repositoryUrl,
        string gitRef,
        CancellationToken ct)
    {
        var (owner, repo) = ParseRepository(repositoryUrl);
        if (gitRef is null || !RefPattern.IsMatch(gitRef)
            || gitRef.Contains("..", StringComparison.Ordinal))
        {
            throw new RepositoryAccessException("Invalid repository reference.");
        }

        var apiPath = $"https://{ApiHost}/repos/{owner}/{repo}";
        using var commitResponse = await SendAsync(
            new Uri($"{apiPath}/commits/{Uri.EscapeDataString(gitRef)}"), shaResponse: true, ct);
        EnsureSuccess(commitResponse, "Repository reference could not be resolved.");
        string sha;
        try
        {
            await using var stream = await commitResponse.Content.ReadAsStreamAsync(ct);
            using var bounded = new BoundedReadStream(stream, 128);
            using var reader = new StreamReader(bounded, Encoding.ASCII);
            sha = (await reader.ReadToEndAsync(ct)).Trim();
        }
        catch (SnapshotLimitExceededException)
        {
            throw new RepositoryAccessException("Invalid repository commit response.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new RepositoryAccessException("Invalid repository commit response.");
        }

        if (!ShaPattern.IsMatch(sha))
        {
            throw new RepositoryAccessException("Invalid repository commit response.");
        }

        using var archiveResponse = await SendAsync(
            new Uri($"{apiPath}/tarball/{sha}"), shaResponse: false, ct);
        EnsureSuccess(archiveResponse, "Repository archive could not be downloaded.");
        if (archiveResponse.Content.Headers.ContentLength is long length
            && length > _assessmentOptions.MaxTotalBytes)
        {
            throw new SnapshotLimitExceededException("Compressed repository archive exceeds the size limit.");
        }

        try
        {
            await using var responseStream = await archiveResponse.Content.ReadAsStreamAsync(ct);
            using var compressed = new BoundedReadStream(
                responseStream, _assessmentOptions.MaxTotalBytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            using var rawTar = new BoundedReadStream(gzip, MaximumRawTarBytes);
            using var tar = new TarReader(rawTar, leaveOpen: true);
            var files = await ExtractAsync(tar, ct);
            await rawTar.CopyToAsync(Stream.Null, ct);
            await compressed.CopyToAsync(Stream.Null, ct);
            return new RepositorySnapshot(owner, repo, sha, files);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException
                                   or FormatException or ArgumentException
                                   or OverflowException)
        {
            throw new RepositoryAccessException("Invalid repository archive.");
        }
    }

    private (string Owner, string Repo) ParseRepository(Uri repositoryUrl)
    {
        if (repositoryUrl is null || !repositoryUrl.IsAbsoluteUri
            || !IsAllowedHost(repositoryUrl, _repositoryOptions.AllowedInputHosts)
            || repositoryUrl.UserInfo.Length != 0
            || repositoryUrl.OriginalString.Contains('@')
            || repositoryUrl.Query.Length != 0
            || repositoryUrl.Fragment.Length != 0
            || repositoryUrl.AbsolutePath.Contains('%'))
        {
            throw new RepositoryAccessException("Invalid repository URL.");
        }

        var segments = repositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || !OwnerPattern.IsMatch(segments[0]))
        {
            throw new RepositoryAccessException("Invalid repository URL.");
        }

        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        if (!RepoPattern.IsMatch(repo))
        {
            throw new RepositoryAccessException("Invalid repository URL.");
        }

        return (segments[0], repo);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri initialUri, bool shaResponse, CancellationToken ct)
    {
        var uri = initialUri;
        for (var redirects = 0; ; redirects++)
        {
            if (!IsAllowedHost(uri, _repositoryOptions.AllowedDownloadHosts)
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            {
                throw new RepositoryAccessException("Repository download host is not allowed.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("hackathon-assessment-api");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (shaResponse)
            {
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));
            }

            if (redirects == 0 && uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_repositoryOptions.GitHubToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", _repositoryOptions.GitHubToken);
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException)
            {
                throw new RepositoryAccessException("Repository request failed.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RepositoryAccessException("Repository request timed out.");
            }

            if (response.StatusCode is not (HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 3 || location is null)
            {
                throw new RepositoryAccessException("Repository redirect limit exceeded or invalid.");
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static bool IsAllowedHost(Uri uri, string[] hosts) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    private static void EnsureSuccess(HttpResponseMessage response, string message)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new RepositoryAccessException(message);
        }
    }

    private async Task<IReadOnlyDictionary<string, SnapshotFile>> ExtractAsync(
        TarReader tar, CancellationToken ct)
    {
        var files = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        string? archiveRoot = null;
        long includedBytes = 0;
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(copyData: false, ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            var segments = PathSegments(entry.Name);
            if (segments is null)
            {
                continue;
            }

            archiveRoot ??= segments[0];
            if (segments[0] != archiveRoot || segments.Length < 2
                || entry.EntryType is not (TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                || segments[..^1].Any(IsExcludedFolder))
            {
                continue;
            }

            var path = string.Join('/', segments[1..]);
            if (files.ContainsKey(path))
            {
                throw new RepositoryAccessException("Duplicate repository archive path.");
            }

            if (entry.DataStream is null || entry.Length < 0)
            {
                throw new RepositoryAccessException("Invalid repository archive entry.");
            }

            var prefix = new byte[(int)Math.Min(8192, entry.Length)];
            await entry.DataStream.ReadExactlyAsync(prefix, ct);
            var isUnicode = prefix.Length >= 2
                && ((prefix[0] == 0xFF && prefix[1] == 0xFE)
                    || (prefix[0] == 0xFE && prefix[1] == 0xFF));
            if (!isUnicode && prefix.AsSpan().Contains((byte)0))
            {
                continue;
            }

            if (entry.Length > int.MaxValue || entry.Length > _assessmentOptions.MaxTotalBytes
                || entry.Length > _assessmentOptions.MaxTotalBytes - includedBytes)
            {
                if (await IsValidOversizedTextAsync(entry.DataStream, prefix, ct))
                {
                    throw new SnapshotLimitExceededException("Repository text size limit exceeded.");
                }

                continue;
            }

            using var memory = new MemoryStream((int)entry.Length);
            await memory.WriteAsync(prefix, ct);
            await entry.DataStream.CopyToAsync(memory, ct);
            if (memory.Length != entry.Length)
            {
                throw new RepositoryAccessException("Invalid repository archive entry.");
            }

            var content = DecodeText(memory.ToArray());
            if (content is null)
            {
                continue;
            }

            if (files.Count >= _assessmentOptions.MaxFiles)
            {
                throw new SnapshotLimitExceededException("Repository text file limit exceeded.");
            }

            includedBytes += entry.Length;
            files.Add(path, new SnapshotFile(path, content, Classify(path)));
        }

        return files;
    }

    private static async Task<bool> IsValidOversizedTextAsync(
        Stream data, byte[] prefix, CancellationToken ct)
    {
        var (encoding, offset) = TextEncoding(prefix);
        var decoder = encoding.GetDecoder();
        var chars = new char[8192];
        var chunk = new byte[8192];
        try
        {
            decoder.GetChars(prefix.AsSpan(offset), chars, flush: false);
            int read;
            while ((read = await data.ReadAsync(chunk, ct)) > 0)
            {
                decoder.GetChars(chunk.AsSpan(0, read), chars, flush: false);
            }

            decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string? DecodeText(byte[] bytes)
    {
        var (encoding, offset) = TextEncoding(bytes);
        try
        {
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static (Encoding Encoding, int Offset) TextEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return (new UTF8Encoding(false, true), 3);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return (new UnicodeEncoding(false, false, true), 2);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return (new UnicodeEncoding(true, false, true), 2);
        }

        return (new UTF8Encoding(false, true), 0);
    }

    private static string[]? PathSegments(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '/' || path.Contains('\\')
            || path.Contains('\0') || (path.Length > 1 && path[1] == ':'))
        {
            return null;
        }

        var segments = path.TrimEnd('/').Split('/');
        return segments.Any(segment => segment is "" or "." or "..")
            ? null : segments;
    }

    private static bool IsExcludedFolder(string segment) =>
        segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".venv", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("dist", StringComparison.OrdinalIgnoreCase);

    private static SnapshotFileRole Classify(string path)
    {
        var lower = path.ToLowerInvariant();
        var name = lower[(lower.LastIndexOf('/') + 1)..];
        if (lower.StartsWith(".github/workflows/", StringComparison.Ordinal))
        {
            return SnapshotFileRole.Workflow;
        }

        if (lower.StartsWith("tests/", StringComparison.Ordinal)
            || lower.StartsWith("test/", StringComparison.Ordinal)
            || lower.Contains("/tests/", StringComparison.Ordinal)
            || name.Contains(".test.", StringComparison.Ordinal)
            || name.Contains(".spec.", StringComparison.Ordinal))
        {
            return SnapshotFileRole.Test;
        }

        if (lower.StartsWith("docs/", StringComparison.Ordinal)
            || name is "readme.md" or "license" or "changelog.md"
            || lower.EndsWith(".md", StringComparison.Ordinal))
        {
            return SnapshotFileRole.Docs;
        }

        if (name is "dockerfile" or "makefile"
            || lower.EndsWith(".json", StringComparison.Ordinal)
            || lower.EndsWith(".yaml", StringComparison.Ordinal)
            || lower.EndsWith(".yml", StringComparison.Ordinal)
            || lower.EndsWith(".config", StringComparison.Ordinal)
            || lower.EndsWith(".csproj", StringComparison.Ordinal)
            || lower.EndsWith(".sln", StringComparison.Ordinal)
            || lower.EndsWith(".slnx", StringComparison.Ordinal)
            || name.StartsWith(".env", StringComparison.Ordinal))
        {
            return SnapshotFileRole.Config;
        }

        if (name.EndsWith(".cs", StringComparison.Ordinal)
            || name.EndsWith(".js", StringComparison.Ordinal)
            || name.EndsWith(".ts", StringComparison.Ordinal)
            || name.EndsWith(".py", StringComparison.Ordinal)
            || name.EndsWith(".go", StringComparison.Ordinal))
        {
            return SnapshotFileRole.App;
        }

        return SnapshotFileRole.Other;
    }
}
