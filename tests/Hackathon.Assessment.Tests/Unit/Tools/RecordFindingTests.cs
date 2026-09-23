using System.Security.Cryptography;
using System.Text;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Tools;
using Hackathon.Assessment.Tests.Unit.Snapshot;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Tools;

public sealed class RecordFindingTests
{
    [Fact]
    public void AcceptsVerifiedMaskedSnippetAndPinnedUrl()
    {
        var ctx = Context(("src/my file.cs", "password=abcdefghijk"));
        ctx.EvidenceLedger.MarkSeen("src/my file.cs", 1);
        var finding = Valid() with
        {
            File = "src/my file.cs",
            Snippet = "password=abcdefghijk"
        };
        var result = Validator().Record(ctx, finding);
        Assert.True(result.Accepted);
        Assert.NotNull(result.Evidence);
        Assert.Equal("***MASKED***", result.Evidence.Snippet.Replace("password=", ""));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("password=***MASKED***"))), result.Evidence.SnippetSha256);
        Assert.Equal(
            $"https://github.com/org/repo/blob/{SnapshotTestData.CommitSha}/src/my%20file.cs#L1-L1",
            result.Evidence.Url);
        Assert.Single(ctx.EvidenceLedger.Findings);
    }

    [Theory]
    [InlineData("missing", "src/no.cs", 1, 1, "dangerous call();", "Add a concrete input guard to the call.")]
    [InlineData("range", "src/app.cs", 0, 1, "dangerous call();", "Add a concrete input guard to the call.")]
    [InlineData("range", "src/app.cs", 1, 2, "dangerous call();", "Add a concrete input guard to the call.")]
    [InlineData("snippet", "src/app.cs", 1, 1, "different()", "Add a concrete input guard to the call.")]
    [InlineData("generic", "src/app.cs", 1, 1, "dangerous call();", "Consider refactoring.")]
    public void RejectsFirstFailureWithReasonAndFix(
        string kind, string path, int start, int end, string snippet, string recommendation)
    {
        var ctx = Context(("src/app.cs", "dangerous call();"));
        ctx.EvidenceLedger.MarkSeen("src/app.cs", 1);
        var result = Validator().Record(ctx, Valid() with
        {
            File = path,
            StartLine = start,
            EndLine = end,
            Snippet = snippet,
            Recommendation = recommendation
        });
        Assert.False(result.Accepted, kind);
        Assert.NotNull(result.Reason);
        Assert.NotNull(result.FixHint);
        Assert.Equal(1, result.RejectedCount);
    }

    [Fact]
    public void CitesEveryLineAndRejects121LineDifference()
    {
        var text = string.Join('\n', Enumerable.Range(1, 122).Select(i => $"line {i}"));
        var ctx = Context(("big.cs", text));
        ctx.EvidenceLedger.MarkSeen("big.cs", 1);
        var finding = Valid() with { File = "big.cs", StartLine = 1, EndLine = 121, Snippet = "line 1" };
        Assert.False(Validator().Record(ctx, finding).Accepted);
        foreach (var line in Enumerable.Range(2, 120))
        {
            ctx.EvidenceLedger.MarkSeen("big.cs", line);
        }
        Assert.True(Validator().Record(ctx, finding).Accepted);
        Assert.False(Validator().Record(ctx,
            finding with { EndLine = 122, Title = "Other issue" }).Accepted);
    }

    [Theory]
    [InlineData("severity", "urgent", "kesin", "UseCase §6.1 — example")]
    [InlineData("confidence", "high", "maybe", "UseCase §6.1 — example")]
    [InlineData("standardRef", "high", "kesin", "")]
    public void RequiresValidMetadata(string name, string severity, string confidence, string standardRef)
    {
        var ctx = Context(("src/app.cs", "dangerous call();"));
        ctx.EvidenceLedger.MarkSeen("src/app.cs", 1);
        var result = Validator().Record(ctx, Valid() with
        {
            Severity = severity,
            Confidence = confidence,
            StandardRef = standardRef
        });
        Assert.False(result.Accepted, name);
    }

    [Fact]
    public void SixthFailureIsPermanentEvenWhenFindingIdChanges()
    {
        var ctx = Context(("src/app.cs", "dangerous call();"));
        var validator = Validator();
        var invalid = Valid() with { File = "missing.cs" };
        for (var attempt = 1; attempt < 6; attempt++)
        {
            Assert.False(validator.Record(ctx, invalid with { Id = $"id-{attempt}" })
                .PermanentlyRejected);
        }
        var sixth = validator.Record(ctx, invalid with { Id = "id-6" });
        Assert.True(sixth.PermanentlyRejected);
        Assert.Equal(6, sixth.RejectedCount);
        ctx.EvidenceLedger.MarkSeen("src/app.cs", 1);
        Assert.True(validator.Record(ctx, invalid with { Id = "different" }).PermanentlyRejected);
    }

    [Fact]
    public void VisibilityIsPerEvaluatorLedgerAndFingerprintIncludesMetric()
    {
        var snapshot = SnapshotTestData.Create(("src/app.cs", "dangerous call();"));
        var first = new ToolContext(snapshot, new EvidenceLedger(), MetricId.M01);
        var second = new ToolContext(snapshot, new EvidenceLedger(), MetricId.M01);
        first.EvidenceLedger.MarkSeen("src/app.cs", 1);
        Assert.True(Validator().Record(first, Valid()).Accepted);
        Assert.False(Validator().Record(second, Valid()).Accepted);
        Assert.Empty(second.EvidenceLedger.Findings);
    }

    [Fact]
    public void LedgerTracksConcurrentLineReads()
    {
        var ledger = new EvidenceLedger();
        Parallel.For(1, 121, line => ledger.MarkSeen("src/app.cs", line));
        Assert.True(ledger.HasSeen("src/app.cs", 1, 120));
        Assert.False(ledger.HasSeen("src/other.cs", 1, 120));
    }

    private static ToolContext Context(params (string Path, string Content)[] files) =>
        new(SnapshotTestData.Create(files), new EvidenceLedger(), MetricId.M01);

    private static RecordFindingValidator Validator() => new(new SecretMasker());

    private static FindingInput Valid() =>
        new("f-1", "Unsafe operation", "high", "kesin", "UseCase §6.1 — example",
            "Unsafe call", "Impact", "src/app.cs", 1, 1, "dangerous call();",
            "Add a concrete input guard to the call.");
}
