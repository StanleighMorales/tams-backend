using System.Numerics;
using System.Runtime.InteropServices;

namespace Tams.Api.Kiosk;

/// <summary>
/// 1:N matcher over decrypted templates (FR-1.3, NFR-4). Score is 1 - hamming distance / bits, over the
/// longer of the two blobs (missing bytes count as different), so 1.0 is identical and unrelated
/// blobs sit near 0.5. The best score at or above the threshold wins.
/// ponytail: placeholder until real AS608 captures exist. Raw template bytes are not minutiae, so this
/// ceiling is "noisy re-reads of the same bytes", not true finger-to-finger matching (rotation, pressure,
/// partial prints). Upgrade path: the kiosk uploads the sensor image and the API matches it with
/// SourceAFIS (or the vendor SDK), keeping this cache and the threshold knob (Kiosk:MatchThreshold).
/// </summary>
public static class FingerprintMatcher
{
    /// <summary>Default for Kiosk:MatchThreshold. Calibrate on real scans: raise to cut false accepts.</summary>
    public const double DefaultThreshold = 0.85;

    public static double Score(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < b.Length) { var t = a; a = b; b = t; } // a is the longer
        if (a.Length == 0) return 0;
        var diff = (a.Length - b.Length) * 8L;
        var words = b.Length / 8;
        var wa = MemoryMarshal.Cast<byte, ulong>(a[..(words * 8)]);
        var wb = MemoryMarshal.Cast<byte, ulong>(b[..(words * 8)]);
        for (var i = 0; i < words; i++) diff += BitOperations.PopCount(wa[i] ^ wb[i]);
        for (var i = words * 8; i < b.Length; i++) diff += BitOperations.PopCount((uint)(a[i] ^ b[i]));
        return 1.0 - (double)diff / (a.Length * 8L);
    }

    public static Guid? Match(byte[] probe, IReadOnlyList<(Guid StudentId, byte[] Template)> candidates, double threshold)
    {
        Guid? best = null;
        var bestScore = threshold;
        foreach (var (id, t) in candidates)
        {
            var s = Score(probe, t);
            if (s >= bestScore) { bestScore = s; best = id; }
        }
        return best;
    }
}
