namespace Tams.Api.Tests;

/// <summary>
/// Synthetic fingerprint images, AS608-sized (256 x 288, 8-bit gray, 500 dpi), made in code so no biometric data is
/// committed. Each finger is a ridge field with its own orientation, ridge period, a spiral core and a few ridge
/// dislocations (these make the endings and bifurcations SourceAFIS looks for). A rescan is the same finger shifted by a few pixels with
/// sensor noise. These prove the pipeline, not the error rates: thresholds come from real captures (TAMS-128 phase 1).
/// </summary>
public static class FixtureFingerprints
{
    public const int Width = 256, Height = 288;

    public static byte[] Finger(int fingerSeed, int scanSeed = 0, int dx = 0, int dy = 0, double noise = 0)
    {
        var f = new Random(fingerSeed);
        var theta = f.NextDouble() * Math.PI;
        var period = 8.5 + f.NextDouble() * 2;
        var (cx, cy, turns) = (100 + f.NextDouble() * 56, 110 + f.NextDouble() * 68, 2 + f.Next(3));
        var dislocations = Enumerable.Range(0, 8).Select(_ => (X: 50 + f.NextDouble() * 156, Y: 60 + f.NextDouble() * 170, Sign: f.Next(2) * 2 - 1)).ToArray();
        var (a1, a2, p1, p2) = (2 + f.NextDouble() * 3, 2 + f.NextDouble() * 3, f.NextDouble() * 6.28, f.NextDouble() * 6.28);
        var scan = new Random(scanSeed + 1_000_003);
        var img = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                double u = x + dx, v = y + dy;
                var phase = (u * Math.Cos(theta) + v * Math.Sin(theta)) / period
                    + a1 * Math.Sin(u / 37 + p1) + a2 * Math.Sin(v / 29 + p2)
                    + turns * Math.Atan2(v - cy, u - cx) / (2 * Math.PI);
                foreach (var d in dislocations) phase += d.Sign * Math.Atan2(v - d.Y, u - d.X) / (2 * Math.PI);
                double px = 128 + 120 * Math.Cos(2 * Math.PI * phase);
                var inside = Math.Pow((u - 128) / 120, 2) + Math.Pow((v - 144) / 136, 2) <= 1;
                if (!inside) px = 255;
                else if (noise > 0) px += (scan.NextDouble() - 0.5) * 2 * noise;
                img[y * Width + x] = (byte)Math.Clamp(px, 0, 255);
            }
        return img;
    }
}
