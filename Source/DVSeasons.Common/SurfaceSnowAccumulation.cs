using System;

namespace DVSeasons.Core
{
    // CPU albedo compositing only. A fixed periodic mask makes patches grow and
    // retreat at the same locations, independently of frame rate or season direction.
    public static class SurfaceSnowAccumulation
    {
        public static float Coverage(float snow, bool roof)
        {
            if (float.IsNaN(snow) || snow <= 0) return 0;
            if (snow >= 1) return roof ? 0.98f : 0.95f;
            if (snow <= 0.28f) return Smooth(snow / 0.28f) * 0.22f;
            if (snow <= 0.62f) return 0.22f + Smooth((snow - 0.28f) / 0.34f) * 0.43f;
            return 0.65f + Smooth((snow - 0.62f) / 0.38f) * (roof ? 0.33f : 0.30f);
        }

        public static float Weight(byte rank, float coverage)
        {
            if (coverage <= 0) return 0;
            return Smooth((coverage - (rank + 0.5f) / 256f + 0.035f) / 0.07f);
        }

        public static byte[] CreateRanks(int size)
        {
            if (size < 2) throw new ArgumentOutOfRangeException(nameof(size));
            var values = new int[size * size];
            var histogram = new int[4096];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = x / (float)size;
                var v = y / (float)size;
                var noise = Noise(u, v, 5) * 0.60f + Noise(u, v, 13) * 0.28f + Noise(u, v, 31) * 0.12f;
                var value = Math.Min(4095, Math.Max(0, (int)(noise * 4095)));
                values[y * size + x] = value;
                histogram[value]++;
            }
            var mapping = new byte[4096];
            var count = 0;
            for (var i = 0; i < histogram.Length; i++)
            {
                mapping[i] = (byte)Math.Min(255, (count + histogram[i] / 2) * 256 / values.Length);
                count += histogram[i];
            }
            var result = new byte[values.Length];
            for (var i = 0; i < result.Length; i++) result[i] = mapping[values[i]];
            return result;
        }

        private static float Noise(float u, float v, int period)
        {
            var x = u * period;
            var y = v * period;
            var ix = (int)x;
            var iy = (int)y;
            var tx = Smooth(x - ix);
            var ty = Smooth(y - iy);
            var a = Hash(ix % period, iy % period, period);
            var b = Hash((ix + 1) % period, iy % period, period);
            var c = Hash(ix % period, (iy + 1) % period, period);
            var d = Hash((ix + 1) % period, (iy + 1) % period, period);
            return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
        }

        private static float Hash(int x, int y, int salt)
        {
            unchecked
            {
                uint n = (uint)(x * 374761393 + y * 668265263 + salt * 1274126177);
                n = (n ^ (n >> 13)) * 1274126177u;
                return (n ^ (n >> 16)) / (float)uint.MaxValue;
            }
        }

        private static float Smooth(float value)
        {
            var t = Math.Max(0, Math.Min(1, value));
            return t * t * (3 - 2 * t);
        }
    }
}
