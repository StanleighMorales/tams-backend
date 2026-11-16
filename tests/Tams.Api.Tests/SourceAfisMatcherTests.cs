using System.Diagnostics;
using SourceAFIS;
using Tams.Api.Kiosk;
using Xunit.Abstractions;
using static Tams.Api.Tests.FixtureFingerprints;

namespace Tams.Api.Tests;

public class SourceAfisMatcherTests(ITestOutputHelper output)
{
    static FingerprintTemplate Tpl(byte[] img) => SourceAfisMatcher.Extract(img, Width, Height);

    static double Score(FingerprintTemplate probe, FingerprintTemplate candidate) => new SourceAFIS.FingerprintMatcher(probe).Match(candidate);

    [Fact]
    public void Same_finger_scanned_twice_scores_far_above_the_threshold()
    {
        var enrolled = Tpl(Finger(1, scanSeed: 1, noise: 10));
        var rescan = Tpl(Finger(1, scanSeed: 2, dx: 6, dy: -4, noise: 10));
        var s = Score(rescan, enrolled);
        output.WriteLine($"same finger: {s:F1}");
        Assert.True(s >= SourceAfisMatcher.DefaultThreshold * 1.5, $"same-finger score {s}");
    }

    [Fact]
    public void A_different_finger_stays_below_the_threshold()
    {
        var enrolled = Tpl(Finger(1, noise: 10));
        for (var other = 2; other <= 8; other++)
        {
            var s = Score(Tpl(Finger(other, noise: 10)), enrolled);
            Assert.True(s < SourceAfisMatcher.DefaultThreshold, $"finger {other} scored {s}");
        }
    }

    [Fact]
    public void Match_picks_the_right_student_or_none_and_survives_a_serialize_round_trip()
    {
        var ids = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToArray();
        var cands = ids.Select((id, i) => (id, SourceAfisMatcher.Parse(Tpl(Finger(10 + i, noise: 6)).ToByteArray()))).ToList();

        Assert.Equal(ids[3], SourceAfisMatcher.Match(Tpl(Finger(13, scanSeed: 5, dx: 2, dy: 2, noise: 6)), cands, SourceAfisMatcher.DefaultThreshold));
        Assert.Null(SourceAfisMatcher.Match(Tpl(Finger(99, noise: 10)), cands, SourceAfisMatcher.DefaultThreshold));
        Assert.Null(SourceAfisMatcher.Match(Tpl(Finger(13)), [], SourceAfisMatcher.DefaultThreshold));
    }

    [Fact]
    public void Bad_image_sizes_are_rejected_without_decoding_anything()
    {
        Assert.Throws<ArgumentException>(() => SourceAfisMatcher.Extract(new byte[100], Width, Height));
        Assert.Throws<ArgumentException>(() => SourceAfisMatcher.Extract(new byte[10], 10, 1));
        Assert.Null(SourceAfisMatcher.TryExtract(Convert.ToBase64String(new byte[100]), Width, Height, out var err));
        Assert.NotNull(err);
        Assert.Null(SourceAfisMatcher.TryExtract("!!!", Width, Height, out err));
        Assert.Null(SourceAfisMatcher.TryExtract(Convert.ToBase64String(Finger(1)), null, null, out err));
    }

    [Fact]
    public void TryExtract_builds_a_template_from_base64_raw_pixels()
    {
        var t = SourceAfisMatcher.TryExtract(Convert.ToBase64String(Finger(1, noise: 10)), Width, Height, out var err);
        Assert.Null(err);
        Assert.NotNull(t);
        Assert.True(Score(Tpl(Finger(1, scanSeed: 3, noise: 10)), t!) >= SourceAfisMatcher.DefaultThreshold);
    }

    /// <summary>NFR-4: 1:N over 2,000 templates in 1.5 s or less (p95). 40 distinct fixture fingers repeated 50 times: cost per candidate does not depend on the repeat.</summary>
    [Fact]
    public void Matching_against_2000_templates_has_a_p95_under_1_5_seconds()
    {
        var distinct = Enumerable.Range(0, 40).Select(i => SourceAfisMatcher.Parse(Tpl(Finger(100 + i, noise: 10)).ToByteArray())).ToList();
        var cands = Enumerable.Range(0, 2000).Select(i => (Guid.NewGuid(), distinct[i % distinct.Count])).ToList();
        var probe = Tpl(Finger(139, scanSeed: 9, dx: 3, dy: 2, noise: 10)); // the same finger as distinct[39]

        SourceAfisMatcher.Match(probe, cands, SourceAfisMatcher.DefaultThreshold); // warm up
        var times = new List<double>();
        Guid? hit = null;
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            hit = SourceAfisMatcher.Match(probe, cands, SourceAfisMatcher.DefaultThreshold);
            times.Add(sw.Elapsed.TotalSeconds);
        }
        times.Sort();
        var p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        output.WriteLine($"2,000 templates: median {times[times.Count / 2] * 1000:F0} ms, p95 {p95 * 1000:F0} ms, {Environment.ProcessorCount} cores");
        Assert.NotNull(hit);
        Assert.True(p95 <= 1.5, $"p95 {p95:F2} s");
    }
}
