using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Tools;

public sealed class EvidenceLedger
{
    private readonly ConcurrentDictionary<string, BitArray> _seen =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _rejections =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Finding> _findings =
        new(StringComparer.Ordinal);
    private int _rejectedCount;

    public int RejectedCount => Volatile.Read(ref _rejectedCount);

    public void MarkSeen(string path, int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line);
        var bits = _seen.GetOrAdd(path, static _ => new BitArray(64));
        lock (bits)
        {
            if (line >= bits.Length)
            {
                bits.Length = Math.Max(bits.Length * 2, line + 1);
            }

            bits[line] = true;
        }
    }

    public bool HasSeen(string path, int startLine, int endLine)
    {
        if (!_seen.TryGetValue(path, out var bits) || startLine < 1 || endLine < startLine)
        {
            return false;
        }

        lock (bits)
        {
            if (endLine >= bits.Length)
            {
                return false;
            }

            for (var line = startLine; line <= endLine; line++)
            {
                if (!bits[line])
                {
                    return false;
                }
            }
        }

        return true;
    }

    public bool IsPermanentlyRejected(string fingerprint) =>
        _rejections.TryGetValue(fingerprint, out var attempts) && attempts >= 6;

    public bool RegisterRejection(string fingerprint)
    {
        Interlocked.Increment(ref _rejectedCount);
        return _rejections.AddOrUpdate(
            fingerprint, 1, static (_, prior) => Math.Min(6, prior + 1)) >= 6;
    }

    public int RegisterRejectionAndCount(string fingerprint)
    {
        RegisterRejection(fingerprint);
        return RejectedCount;
    }

    public void Record(string fingerprint, Finding finding) =>
        _findings.TryAdd(fingerprint, finding);

    public ImmutableArray<Finding> Findings => [.. _findings.Values];
}
