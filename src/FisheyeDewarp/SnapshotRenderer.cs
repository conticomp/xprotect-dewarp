using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FisheyeDewarp
{
    /// <summary>
    /// CPU version of the dewarp shader, run on the full-resolution frame so a snapshot carries more
    /// detail than the tile shows on screen.
    /// </summary>
    internal static class SnapshotRenderer
    {
        /// <summary>Copies the fisheye frame's pixels. Call on the UI thread; the result can be rendered on any thread.</summary>
        public static SourceFrame Capture(BitmapSource frame)
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new int[bgra.PixelWidth * bgra.PixelHeight];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return new SourceFrame(pixels, bgra.PixelWidth, bgra.PixelHeight);
        }

        public static BitmapSource Render(SourceFrame src, double[,] m, double tanX, double tanY, double lensHalfFov, int width, int height)
        {
            var output = new int[width * height];
            double tanHalfLens = Math.Tan(lensHalfFov / 2);

            Parallel.For(0, height, y =>
            {
                double sy = ((y + 0.5) / height * 2 - 1) * tanY;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double sx = ((x + 0.5) / width * 2 - 1) * tanX;
                    double n = Math.Sqrt(sx * sx + sy * sy + 1);
                    double vx = sx / n, vy = sy / n, vz = 1 / n;
                    double dx = m[0, 0] * vx + m[0, 1] * vy + m[0, 2] * vz;
                    double dy = m[1, 0] * vx + m[1, 1] * vy + m[1, 2] * vz;
                    double dz = m[2, 0] * vx + m[2, 1] * vy + m[2, 2] * vz;

                    double theta = Math.Acos(Math.Max(-1, Math.Min(1, dz)));
                    if (theta > lensHalfFov)
                    {
                        output[row + x] = unchecked((int)0xFF000000);
                        continue;
                    }
                    double rn = Math.Tan(theta / 2) / tanHalfLens;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    double ix = 0.5 + (len > 1e-9 ? dx / len : 0) * rn * 0.5;
                    double iy = 0.5 + (len > 1e-9 ? dy / len : 0) * rn * 0.5;
                    output[row + x] = src.Sample(ix * src.Width - 0.5, iy * src.Height - 0.5);
                }
            });

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>A CPU dewarp ready to run on a worker thread.</summary>
    internal sealed class RenderJob
    {
        private readonly SourceFrame _source;
        private readonly double[,] _rotation;
        private readonly double _tanX, _tanY, _lensHalfFov;
        private long _renderMs;

        public RenderJob(SourceFrame source, double[,] rotation, double tanX, double tanY, double lensHalfFov, int width, int height)
        {
            _source = source;
            _rotation = rotation;
            _tanX = tanX;
            _tanY = tanY;
            _lensHalfFov = lensHalfFov;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public BitmapSource Render()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BitmapSource result = SnapshotRenderer.Render(_source, _rotation, _tanX, _tanY, _lensHalfFov, Width, Height);
            _renderMs = sw.ElapsedMilliseconds;
            return result;
        }

        public override string ToString() =>
            $"{Width}x{Height} from {_source.Width}x{_source.Height} (render {_renderMs} ms)";
    }

    internal sealed class SourceFrame
    {
        private readonly int[] _pixels;

        public SourceFrame(int[] pixels, int width, int height)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Bilinear sample at pixel coordinates, clamped to the frame edges.</summary>
        public int Sample(double x, double y)
        {
            x = Math.Max(0, Math.Min(Width - 1.001, x));
            y = Math.Max(0, Math.Min(Height - 1.001, y));
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            int i = y0 * Width + x0;
            int p00 = _pixels[i], p10 = _pixels[i + 1], p01 = _pixels[i + Width], p11 = _pixels[i + Width + 1];

            int result = unchecked((int)0xFF000000);
            for (int shift = 0; shift < 24; shift += 8)
            {
                double top = ((p00 >> shift) & 0xFF) * (1 - fx) + ((p10 >> shift) & 0xFF) * fx;
                double bottom = ((p01 >> shift) & 0xFF) * (1 - fx) + ((p11 >> shift) & 0xFF) * fx;
                result |= (int)(top * (1 - fy) + bottom * fy + 0.5) << shift;
            }
            return result;
        }
    }
}
