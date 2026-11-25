using SourceAFIS;

namespace Tams.Api.Kiosk;

/// <summary>
/// Real 1:N matcher (FR-1.3, NFR-4, TAMS-128). The kiosk uploads the sensor IMAGE; it is turned into a SourceAFIS
/// template in memory and only that template is ever stored (encrypted). The image bytes are never written to disk,
/// the database or logs, and nothing here keeps a reference to them.
/// Score is SourceAFIS similarity (about 0 for strangers). 40 is the library's documented 1-in-100,000 false match
/// point. ponytail: PROVISIONAL default, not calibrated on AS608 captures (TAMS-128 phase 1, blocked on TAMS-52).
/// Tune with Kiosk:SourceAfisThreshold once real captures exist.
/// </summary>
public static class SourceAfisMatcher
{
    public const string Format = "SOURCEAFIS_V1";
    public const double DefaultThreshold = 40;
    public const int MaxImageBytes = 512 * 1024;
    const double SensorDpi = 500; // AS608 (256x288 px) and ZK9500 are both 500 dpi optical; ponytail: make it config if a sensor with another dpi is added

    /// <summary>
    /// Builds a template from a raw 8-bit grayscale image, one byte per pixel, row by row (what the AS608 sends).
    /// Encoded formats (PNG, JPEG) are not accepted on purpose: SourceAFIS would decode them with ImageSharp 2.1.3,
    /// which has known advisories, and we never need it for the sensor's raw frame.
    /// </summary>
    /// <exception cref="ArgumentException">The size is out of range or the length is not width * height.</exception>
    public static FingerprintTemplate Extract(byte[] pixels, int width, int height)
    {
        if (width is < 64 or > 1024 || height is < 64 or > 1024 || pixels.Length != width * height)
            throw new ArgumentException("Raw image length must equal width * height (each 64 to 1024).");
        return new FingerprintTemplate(new FingerprintImage(width, height, pixels, new FingerprintImageOptions { Dpi = SensorDpi }));
    }

    /// <summary>
    /// Decodes the request's base64 image and extracts a template; null plus an error message when it is unusable.
    /// The pixel buffer is zeroed before returning (belt and braces: nothing else keeps it).
    /// </summary>
    public static FingerprintTemplate? TryExtract(string image, int? width, int? height, out string? error)
    {
        error = null;
        if (width is null || height is null) { error = "width and height are required with image."; return null; }
        var buf = new byte[Math.Min(MaxImageBytes, image.Length)];
        if (!Convert.TryFromBase64String(image, buf, out var n)) { error = $"Not valid base64 of 1 to {MaxImageBytes} bytes."; return null; }
        var pixels = buf[..n];
        try { return Extract(pixels, width.Value, height.Value); }
        catch (ArgumentException ex) { error = ex.Message; return null; }
        finally { Array.Clear(buf); Array.Clear(pixels); }
    }

    public static FingerprintTemplate Parse(byte[] serialized) => new(serialized);

    /// <summary>Best candidate at or above the threshold, or null.</summary>
    public static Guid? Match(FingerprintTemplate probe, IReadOnlyList<(Guid StudentId, FingerprintTemplate Template)> candidates, double threshold)
    {
        var matcher = new SourceAFIS.FingerprintMatcher(probe);
        var scores = new double[candidates.Count];
        Parallel.For(0, candidates.Count, i => scores[i] = matcher.Match(candidates[i].Template));
        Guid? best = null;
        var bestScore = threshold;
        for (var i = 0; i < scores.Length; i++)
            if (scores[i] >= bestScore) { bestScore = scores[i]; best = candidates[i].StudentId; }
        return best;
    }
}
