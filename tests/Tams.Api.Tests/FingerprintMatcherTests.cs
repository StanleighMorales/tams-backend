using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Tams.Api.Kiosk;
using Xunit.Abstractions;

namespace Tams.Api.Tests;

public class FingerprintMatcherTests(ITestOutputHelper output)
{
    const int Size = 512; // AS608 template size

    static byte[] Random(int seed, int size = Size)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Flips <paramref name="fraction"/> of the bits, the simulator's "noisy rescan".</summary>
    static byte[] Noisy(byte[] src, double fraction, int seed)
    {
        var r = new Random(seed);
        var b = (byte[])src.Clone();
        var flips = (int)(b.Length * 8 * fraction);
        var idx = Enumerable.Range(0, b.Length * 8).OrderBy(_ => r.Next()).Take(flips);
        foreach (var i in idx) b[i / 8] ^= (byte)(1 << (i % 8));
        return b;
    }

    [Fact]
    public void Identical_templates_score_one_and_unrelated_score_near_half()
    {
        var a = Random(1);
        Assert.Equal(1.0, FingerprintMatcher.Score(a, a));
        Assert.InRange(FingerprintMatcher.Score(a, Random(2)), 0.45, 0.55);
    }

    [Fact]
    public void Same_finger_with_noise_matches_and_different_finger_does_not()
    {
        var enrolled = Enumerable.Range(0, 50).Select(i => (Guid.NewGuid(), Random(i))).ToList();
        var probe = Noisy(enrolled[17].Item2, 0.05, 99);
        Assert.Equal(enrolled[17].Item1, FingerprintMatcher.Match(probe, enrolled, FingerprintMatcher.DefaultThreshold));
        Assert.Null(FingerprintMatcher.Match(Random(1000), enrolled, FingerprintMatcher.DefaultThreshold));
    }

    [Fact]
    public void Threshold_edge_is_inclusive()
    {
        var a = Random(3);
        var b = (byte[])a.Clone();
        for (var i = 0; i < 4; i++) b[i] ^= 0xFF; // 32 of 4096 bits differ: score 0.9921875
        var id = Guid.NewGuid();
        var score = FingerprintMatcher.Score(a, b);
        Assert.Equal(id, FingerprintMatcher.Match(b, [(id, a)], score));
        Assert.Null(FingerprintMatcher.Match(b, [(id, a)], score + 1e-9));
    }

    [Fact]
    public void Length_mismatch_counts_missing_bytes_as_different()
    {
        var a = Random(4);
        Assert.Equal(0.5, FingerprintMatcher.Score(a[..(Size / 2)], a), 6);
    }

    [Fact]
    public void Best_of_several_candidates_wins()
    {
        var a = Random(5);
        var near = Guid.NewGuid();
        var nearer = Guid.NewGuid();
        var list = new List<(Guid, byte[])> { (near, Noisy(a, 0.08, 1)), (nearer, Noisy(a, 0.02, 2)) };
        Assert.Equal(nearer, FingerprintMatcher.Match(a, list, 0.8));
    }

    [Fact]
    public void Match_over_2000_templates_is_within_1_5_seconds()
    {
        var provider = DataProtectionProvider.Create("tams-test");
        var protector = provider.CreateProtector("bench");
        var plain = Enumerable.Range(0, 2000).Select(i => Random(i + 10)).ToList();
        var cipher = plain.Select(protector.Protect).ToList();

        // Cold path (decrypt all, then match): what the cache saves on every scan after the first.
        var cold = Stopwatch.StartNew();
        var decrypted = cipher.Select((c, i) => (Guid.NewGuid(), protector.Unprotect(c))).ToList();
        var probe = Noisy(plain[1234], 0.05, 7);
        var hit = FingerprintMatcher.Match(probe, decrypted, FingerprintMatcher.DefaultThreshold);
        cold.Stop();
        Assert.Equal(decrypted[1234].Item1, hit);

        // Warm path (cached plaintext): the per-scan cost.
        var warm = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) FingerprintMatcher.Match(probe, decrypted, FingerprintMatcher.DefaultThreshold);
        warm.Stop();
        output.WriteLine($"2000 templates: cold decrypt+match {cold.ElapsedMilliseconds} ms, warm match {warm.Elapsed.TotalMilliseconds / 20:F2} ms");

        Assert.True(cold.Elapsed < TimeSpan.FromSeconds(1.5), $"cold took {cold.ElapsedMilliseconds} ms");
        Assert.True(warm.Elapsed.TotalMilliseconds / 20 < 1500);
    }
}
