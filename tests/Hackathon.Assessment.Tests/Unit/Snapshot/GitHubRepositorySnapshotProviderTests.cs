using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Snapshot;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Snapshot;

public sealed class GitHubRepositorySnapshotProviderTests
{
    private const string Token = "synthetic-test-token";
    private static readonly Uri RepositoryUrl = new("https://github.com/org/repo.git");

    [Fact]
    public async Task PinsRawShaAndDownloadsCodeloadWithoutForwardingToken()
    {
        var archive = Archive(("root/src/file.cs", TarEntryType.RegularFile, Encoding.UTF8.GetBytes("hello")));
        var handler = new ArchiveHandler(archive);
        var snapshot = await Provider(handler, token: Token).GetAsync(
            RepositoryUrl, "main", CancellationToken.None);

        Assert.Equal("org", snapshot.Owner);
        Assert.Equal("repo", snapshot.Repo);
        Assert.Equal(SnapshotTestData.CommitSha, snapshot.CommitSha);
        Assert.Equal("hello", snapshot.Files["src/file.cs"].Content);
        Assert.Equal(SnapshotFileRole.App, snapshot.Files["src/file.cs"].Role);
        Assert.Equal("github", handler.ClientName);
        Assert.Equal(
            "https://api.github.com/repos/org/repo/commits/main", handler.Requests[0].Url);
        Assert.Contains("application/vnd.github.sha", handler.Requests[0].Accept);
        Assert.Equal($"https://api.github.com/repos/org/repo/tarball/{SnapshotTestData.CommitSha}",
            handler.Requests[1].Url);
        Assert.Equal(Token, handler.Requests[0].Bearer);
        Assert.Equal(Token, handler.Requests[1].Bearer);
        Assert.Null(handler.Requests[2].Bearer);
        Assert.Equal("codeload.github.com", new Uri(handler.Requests[2].Url).Host);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("hackathon-assessment-api", request.UserAgent);
            Assert.Equal("2022-11-28", request.ApiVersion);
            Assert.DoesNotContain(Token, request.Url, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task MapsAccessFailuresWithoutLeakingQueryOrToken(HttpStatusCode status)
    {
        var handler = new ArchiveHandler([], commitStatus: status);
        var exception = await Assert.ThrowsAsync<RepositoryAccessException>(() =>
            Provider(handler, token: Token).GetAsync(RepositoryUrl, "main", CancellationToken.None));
        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('?', exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task MapsArchiveAccessFailuresWithoutLeakingSignedRedirect(HttpStatusCode status)
    {
        var handler = new ArchiveHandler([], archiveStatus: status,
            location: new Uri("https://codeload.github.com/archive?signature=private"));
        var exception = await Assert.ThrowsAsync<RepositoryAccessException>(() =>
            Provider(handler, token: Token).GetAsync(RepositoryUrl, "main", CancellationToken.None));
        Assert.DoesNotContain("signature", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.Requests[^1].Bearer);
    }

    [Fact]
    public async Task RejectsInvalidShaAndNeverFetchesArchive()
    {
        var handler = new ArchiveHandler([], sha: "ABCDEF" + SnapshotTestData.CommitSha[6..]);
        await Assert.ThrowsAsync<RepositoryAccessException>(() =>
            Provider(handler).GetAsync(RepositoryUrl, "main", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://github.com/org/repo")]
    [InlineData("https://127.0.0.1/org/repo")]
    [InlineData("https://example.com/org/repo")]
    [InlineData("https://github.com:8443/org/repo")]
    [InlineData("https://github.com/org/repo?secret=sensitive")]
    [InlineData("https://@github.com/org/repo")]
    [InlineData("https://u:p@github.com/org/repo")]
    [InlineData("https://github.com/org/%2e%2e")]
    public async Task RejectsInvalidInputBeforeNetwork(string url)
    {
        var handler = new ArchiveHandler([]);
        await Assert.ThrowsAsync<RepositoryAccessException>(() =>
            Provider(handler).GetAsync(new Uri(url), "main", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsDisallowedOrHttpRedirectWithoutRequestingTarget()
    {
        foreach (var target in new[]
        {
            "https://downloads.example.org/signed?token=private",
            "http://codeload.github.com/signed",
            "https://127.0.0.1/archive"
        })
        {
            var handler = new ArchiveHandler([], location: new Uri(target));
            var exception = await Assert.ThrowsAsync<RepositoryAccessException>(() =>
                Provider(handler, token: Token).GetAsync(RepositoryUrl, "main", CancellationToken.None));
            Assert.DoesNotContain("token=private", exception.Message, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
        }
    }

    [Fact]
    public async Task RejectsFourthRedirectAndNeverSendsBearerToEvenApiRedirects()
    {
        var handler = new ArchiveHandler([], redirectCount: 4, redirectToApi: true);
        await Assert.ThrowsAsync<RepositoryAccessException>(() =>
            Provider(handler, token: Token).GetAsync(RepositoryUrl, "main", CancellationToken.None));
        Assert.Equal(5, handler.Requests.Count); // commit, tarball and three redirects
        Assert.All(handler.Requests.Skip(2), request => Assert.Null(request.Bearer));
    }

    [Fact]
    public async Task SkipsUnsafeEntriesAndExcludedFoldersButKeepsOtherText()
    {
        var archive = Archive(
            ("root/", TarEntryType.Directory, []),
            ("root/../escape.txt", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/abs\\bad.txt", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("/absolute.txt", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/link", TarEntryType.SymbolicLink, []),
            ("root/hard", TarEntryType.HardLink, []),
            ("root/.git/index", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/node_modules/a", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/.venv/a", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/bin/a", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/obj/a", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/dist/a", TarEntryType.RegularFile, "bad"u8.ToArray()),
            ("root/binary.bin", TarEntryType.RegularFile, [65, 0, 66]),
            ("root/src/keep.txt", TarEntryType.RegularFile, "safe"u8.ToArray()));
        var snapshot = await Provider(new ArchiveHandler(archive)).GetAsync(
            RepositoryUrl, "main", CancellationToken.None);
        Assert.Equal(["src/keep.txt"], snapshot.Files.Keys);
    }

    [Fact]
    public async Task DecodesBomAndIndexesMixedNewlinesWithoutLineCopies()
    {
        var archive = Archive(
            ("root/utf8.txt", TarEntryType.RegularFile,
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\r\nb\rc\n")]),
            ("root/utf16le.txt", TarEntryType.RegularFile,
                [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello\r\nworld")]),
            ("root/utf16be.txt", TarEntryType.RegularFile,
                [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("alpha\rbeta")]));
        var snapshot = await Provider(new ArchiveHandler(archive)).GetAsync(
            RepositoryUrl, "main", CancellationToken.None);
        var file = snapshot.Files["utf8.txt"];
        Assert.Equal("a\r\nb\rc\n", file.Content);
        Assert.Equal([0, 3, 5], file.LineStarts);
        Assert.Equal(3, file.LineCount);
        Assert.True(file.GetLine(1).SequenceEqual("a"));
        Assert.True(file.GetLine(2).SequenceEqual("b"));
        Assert.True(file.GetLine(3).SequenceEqual("c"));
        Assert.Equal("hello\r\nworld", snapshot.Files["utf16le.txt"].Content);
        Assert.Equal([0, 7], snapshot.Files["utf16le.txt"].LineStarts);
        Assert.Equal("alpha\rbeta", snapshot.Files["utf16be.txt"].Content);
        Assert.Equal([0, 6], snapshot.Files["utf16be.txt"].LineStarts);
        var lineStarts = file.LineStarts;
        lineStarts[0] = 900;
        Assert.True(file.GetLine(1).SequenceEqual("a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetLine(0).ToString());
    }

    [Fact]
    public async Task CountsOnlyIncludedTextAndClassifiesRoles()
    {
        var archive = Archive(
            ("root/tests/a.test.ts", TarEntryType.RegularFile, "test"u8.ToArray()),
            ("root/.github/workflows/build.yml", TarEntryType.RegularFile, "ci"u8.ToArray()),
            ("root/docs/readme.md", TarEntryType.RegularFile, "docs"u8.ToArray()),
            ("root/appsettings.json", TarEntryType.RegularFile, "{}"u8.ToArray()),
            ("root/src/a.cs", TarEntryType.RegularFile, "a"u8.ToArray()),
            ("root/asset.txt", TarEntryType.RegularFile, "asset"u8.ToArray()),
            ("root/dist/large.txt", TarEntryType.RegularFile, new byte[2048]),
            ("root/image.png", TarEntryType.RegularFile, [0, 1, 2]));
        var snapshot = await Provider(new ArchiveHandler(archive), maxFiles: 6)
            .GetAsync(RepositoryUrl, "main", CancellationToken.None);
        Assert.Equal(6, snapshot.Files.Count);
        Assert.Equal(SnapshotFileRole.Test, snapshot.Files["tests/a.test.ts"].Role);
        Assert.Equal(SnapshotFileRole.Workflow, snapshot.Files[".github/workflows/build.yml"].Role);
        Assert.Equal(SnapshotFileRole.Docs, snapshot.Files["docs/readme.md"].Role);
        Assert.Equal(SnapshotFileRole.Config, snapshot.Files["appsettings.json"].Role);
        Assert.Equal(SnapshotFileRole.App, snapshot.Files["src/a.cs"].Role);
        Assert.Equal(SnapshotFileRole.Other, snapshot.Files["asset.txt"].Role);
    }

    [Fact]
    public async Task ThrowsAt2001stIncludedFile()
    {
        var entries = Enumerable.Range(0, 2001)
            .Select(index => ($"root/file{index:0000}.txt", TarEntryType.RegularFile,
                (byte[]?)"a"u8.ToArray()))
            .ToArray();
        var archive = Archive(entries);
        var atLimit = await Provider(new ArchiveHandler(Archive(entries[..2000])))
            .GetAsync(RepositoryUrl, "main", CancellationToken.None);
        Assert.Equal(2000, atLimit.Files.Count);
        await Assert.ThrowsAsync<SnapshotLimitExceededException>(() =>
            Provider(new ArchiveHandler(archive)).GetAsync(
                RepositoryUrl, "main", CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCompressedLimitIncludingUnknownContentLength()
    {
        var archive = Archive(("root/readme.txt", TarEntryType.RegularFile, "ok"u8.ToArray()));
        var atLimit = await Provider(new ArchiveHandler(archive), maxBytes: archive.Length)
            .GetAsync(RepositoryUrl, "main", CancellationToken.None);
        Assert.Single(atLimit.Files);
        await Assert.ThrowsAsync<SnapshotLimitExceededException>(() =>
            Provider(new ArchiveHandler(archive, includeContentLength: false),
                    maxBytes: archive.Length - 1)
                .GetAsync(RepositoryUrl, "main", CancellationToken.None));
        await Assert.ThrowsAsync<SnapshotLimitExceededException>(() =>
            Provider(new ArchiveHandler(archive), maxBytes: archive.Length - 1)
                .GetAsync(RepositoryUrl, "main", CancellationToken.None));
    }

    [Fact]
    public async Task RejectsContentLengthAboveDefault50MiBBeforeReadingBody()
    {
        var handler = new ArchiveHandler([], advertisedLength: 52_428_801);
        await Assert.ThrowsAsync<SnapshotLimitExceededException>(() =>
            Provider(handler).GetAsync(RepositoryUrl, "main", CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsIncludedTextByteLimit()
    {
        var archive = Archive(
            ("root/bin/ignored", TarEntryType.RegularFile, new byte[2048]),
            ("root/one.txt", TarEntryType.RegularFile, new byte[120]),
            ("root/two.txt", TarEntryType.RegularFile, new byte[120]));
        await Assert.ThrowsAsync<SnapshotLimitExceededException>(() =>
            Provider(new ArchiveHandler(archive), maxBytes: 180)
                .GetAsync(RepositoryUrl, "main", CancellationToken.None));
    }

    [Fact]
    public async Task InvalidUtf8IsSkippedEvenWhenItExceedsIncludedTextLimit()
    {
        var invalid = Enumerable.Repeat((byte)'a', 9000).ToArray();
        invalid[^1] = 0xFF;
        var archive = Archive(
            ("root/invalid.txt", TarEntryType.RegularFile, invalid),
            ("root/good.txt", TarEntryType.RegularFile, "ok"u8.ToArray()));
        var snapshot = await Provider(new ArchiveHandler(archive), maxBytes: archive.Length)
            .GetAsync(RepositoryUrl, "main", CancellationToken.None);
        Assert.Equal(["good.txt"], snapshot.Files.Keys);
    }

    [Fact]
    public async Task OnlyFirst8192BytesDetermineBinaryNulFiltering()
    {
        var data = Enumerable.Repeat((byte)'a', 8193).ToArray();
        data[^1] = 0;
        var archive = Archive(("root/file.txt", TarEntryType.RegularFile, data));
        var snapshot = await Provider(new ArchiveHandler(archive)).GetAsync(
            RepositoryUrl, "main", CancellationToken.None);
        Assert.Single(snapshot.Files);
        Assert.EndsWith("\0", snapshot.Files["file.txt"].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTarExpansionOver200MiBEvenIfExcluded()
    {
        var archive = Archive(
            ("root/dist/huge", TarEntryType.RegularFile, null));
        await Assert.ThrowsAsync<SnapshotLimitExceededException>(() =>
            Provider(new ArchiveHandler(archive)).GetAsync(
                RepositoryUrl, "main", CancellationToken.None));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void PrivateReservedAndMappedAddressesAreNotPublic(string value) =>
        Assert.False(IpAddressPolicy.IsPublic(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void GlobalAddressesArePublic(string value) =>
        Assert.True(IpAddressPolicy.IsPublic(IPAddress.Parse(value)));

    private static GitHubRepositorySnapshotProvider Provider(
        ArchiveHandler handler, string? token = null, int maxFiles = 2000,
        long maxBytes = 52_428_800)
    {
        var options = new RepositoryOptions { GitHubToken = token };
        return new GitHubRepositorySnapshotProvider(
            handler,
            Options.Create(options),
            Options.Create(new AssessmentOptions { MaxFiles = maxFiles, MaxTotalBytes = maxBytes }));
    }

    private static byte[] Archive(
        params (string Path, TarEntryType Type, byte[]? Content)[] entries)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, type, bytes) in entries)
            {
                var entry = new PaxTarEntry(type, path);
                if (type is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    entry.DataStream = bytes is null
                        ? new ZeroReadStream(201L * 1024 * 1024)
                        : new MemoryStream(bytes, writable: false);
                }
                else if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    entry.LinkName = "../outside";
                }

                writer.WriteEntry(entry);
                entry.DataStream?.Dispose();
            }
        }

        return compressed.ToArray();
    }

    private sealed class ZeroReadStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = (int)Math.Min(buffer.Length, length - _position);
            buffer[..read].Clear();
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Read(buffer.Span);
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CapturedRequest(
        string Url, string? Bearer, string Accept, string UserAgent, string? ApiVersion);

    private sealed class ArchiveHandler(
        byte[] archive,
        HttpStatusCode commitStatus = HttpStatusCode.OK,
        string sha = SnapshotTestData.CommitSha,
        Uri? location = null,
        int redirectCount = 1,
        bool redirectToApi = false,
        bool includeContentLength = true,
        HttpStatusCode archiveStatus = HttpStatusCode.OK,
        long? advertisedLength = null) : HttpMessageHandler, IHttpClientFactory
    {
        private int _archiveRequests;
        public string? ClientName { get; private set; }
        public List<CapturedRequest> Requests { get; } = [];

        public HttpClient CreateClient(string name)
        {
            ClientName = name;
            return new HttpClient(this, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Parameter,
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Headers.UserAgent.ToString(),
                request.Headers.GetValues("X-GitHub-Api-Version").Single()));

            if (request.RequestUri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(commitStatus)
                {
                    Content = new StringContent(sha, Encoding.ASCII)
                });
            }

            _archiveRequests++;
            if (_archiveRequests <= redirectCount)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = location ?? new Uri(
                            $"https://{(redirectToApi ? "api.github.com" : "codeload.github.com")}/archive/{_archiveRequests}")
                    }
                });
            }

            if (archiveStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(archiveStatus)
                {
                    Content = new StringContent("Synthetic denial")
                });
            }

            var content = new ByteArrayContent(archive);
            if (advertisedLength is not null)
            {
                content.Headers.ContentLength = advertisedLength;
            }
            else if (!includeContentLength)
            {
                content.Headers.ContentLength = null;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
