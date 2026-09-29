using System;
using System.Threading.Tasks;

namespace FisheyeDewarp
{
    /// <summary>
    /// A fixed view's dewarp, precomputed: for each output pixel, where it samples the source frame and with
    /// what bilinear weights. Building it costs one lens calculation per pixel; applying it to a frame is just
    /// four reads and a blend, which is what makes exporting every recorded frame affordable.
    /// </summary>
    internal sealed class DewarpMap
    {
        private readonly int[] _offset;   // byte offset of the top-left sample in the source plane; -1 = outside the lens
        private readonly byte[] _fx, _fy; // weight of the right / bottom samples, 0..255
        private readonly int _stride;

        private DewarpMap(int width, int height, int stride)
        {
            Width = width;
            Height = height;
            _stride = stride;
            _offset = new int[width * height];
            _fx = new byte[width * height];
            _fy = new byte[width * height];
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>For a source plane of 4-byte pixels (BGRX) of the given size and stride.</summary>
        public static DewarpMap Build(double[,] m, double tanX, double tanY, double lensHalfFov,
            int width, int height, int srcWidth, int srcHeight, int srcStride)
        {
            var map = new DewarpMap(width, height, srcStride);
            double tanHalfLens = Math.Tan(lensHalfFov / 2);
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (!SnapshotRenderer.MapToSource(m, tanX, tanY, lensHalfFov, tanHalfLens, x, y, width, height, out double ix, out double iy))
                    {
                        map._offset[i] = -1;
                        continue;
                    }
                    double px = Math.Max(0, Math.Min(srcWidth - 1.001, ix * srcWidth - 0.5));
                    double py = Math.Max(0, Math.Min(srcHeight - 1.001, iy * srcHeight - 0.5));
                    int x0 = (int)px, y0 = (int)py;
                    map._offset[i] = y0 * srcStride + x0 * 4;
                    map._fx[i] = (byte)Math.Min(255, (int)((px - x0) * 256));
                    map._fy[i] = (byte)Math.Min(255, (int)((py - y0) * 256));
                }
            });
            return map;
        }

        /// <summary>Dewarp one source frame into a top-down BGRA buffer of Width x Height.</summary>
        public unsafe void Apply(IntPtr source, byte[] output)
        {
            byte* src = (byte*)source;
            int stride = _stride;
            fixed (byte* dstBase = output)
            {
                byte* dstPtr = dstBase;
                Parallel.For(0, Height, y =>
                {
                    byte* d = dstPtr + y * Width * 4;
                    for (int i = y * Width, end = i + Width; i < end; i++, d += 4)
                    {
                        int offset = _offset[i];
                        if (offset < 0)
                        {
                            d[0] = d[1] = d[2] = 0;
                            d[3] = 255;
                            continue;
                        }
                        int fx = _fx[i], fy = _fy[i];
                        int w00 = (256 - fx) * (256 - fy), w10 = fx * (256 - fy), w01 = (256 - fx) * fy, w11 = fx * fy;
                        byte* p00 = src + offset, p10 = p00 + 4, p01 = p00 + stride, p11 = p01 + 4;
                        d[0] = (byte)((p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11 + 32768) >> 16);
                        d[1] = (byte)((p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11 + 32768) >> 16);
                        d[2] = (byte)((p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11 + 32768) >> 16);
                        d[3] = 255;
                    }
                });
            }
        }
    }
}
