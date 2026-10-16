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

        /// <summary>Copies a GDI frame's pixels via LockBits, which is much cheaper than a WPF conversion. Any thread.</summary>
        public static SourceFrame Capture(System.Drawing.Bitmap frame)
        {
            int width = frame.Width, height = frame.Height;
            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            System.Drawing.Imaging.BitmapData data = frame.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new int[width * height];
                if (data.Stride == width * 4)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                else
                    for (int y = 0; y < height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width, width);
                return new SourceFrame(pixels, width, height);
            }
            finally
            {
                frame.UnlockBits(data);
            }
        }

        // A 610x610 still is a few milliseconds of work; spreading it over every core costs more in scheduling
        // than it saves, and Smart Client needs the rest of the machine.
        private static readonly ParallelOptions Threads = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        public static BitmapSource Render(SourceFrame src, double[,] m, double tanX, double tanY, double lensHalfFov, int width, int height)
        {
            RenderMap map = null;
            return Render(src, m, tanX, tanY, lensHalfFov, width, height, ref map);
        }

        /// <summary>
        /// Renders through <paramref name="map"/>, the source position of each output pixel. It is rebuilt only
        /// when the view or size changed, so a steady Live view costs one bilinear sample per pixel and no trig.
        /// </summary>
        public static BitmapSource Render(SourceFrame src, double[,] m, double tanX, double tanY, double lensHalfFov, int width, int height, ref RenderMap map)
        {
            if (map == null || !map.Matches(m, tanX, tanY, lensHalfFov, width, height))
                map = RenderMap.Build(m, tanX, tanY, lensHalfFov, width, height);
            float[] xy = map.Positions;
            var output = new int[width * height];

            Parallel.For(0, height, Threads, y =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    float ix = xy[2 * i];
                    output[i] = float.IsNaN(ix) ? unchecked((int)0xFF000000) : src.SampleAt(ix, xy[2 * i + 1]);
                }
            });

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// Where output pixel (x, y) of a width x height view samples the fisheye image, as fractions 0..1 of the
        /// image. False when the ray falls outside the lens. Same math as the shader.
        /// </summary>
        public static bool MapToSource(double[,] m, double tanX, double tanY, double lensHalfFov, double tanHalfLens,
            int x, int y, int width, int height, out double ix, out double iy)
        {
            double sx = ((x + 0.5) / width * 2 - 1) * tanX;
            double sy = ((y + 0.5) / height * 2 - 1) * tanY;
            double n = Math.Sqrt(sx * sx + sy * sy + 1);
            double vx = sx / n, vy = sy / n, vz = 1 / n;
            double dx = m[0, 0] * vx + m[0, 1] * vy + m[0, 2] * vz;
            double dy = m[1, 0] * vx + m[1, 1] * vy + m[1, 2] * vz;
            double dz = m[2, 0] * vx + m[2, 1] * vy + m[2, 2] * vz;

            double theta = Math.Acos(Math.Max(-1, Math.Min(1, dz)));
            if (theta > lensHalfFov)
            {
                ix = iy = 0;
                return false;
            }
            double rn = Math.Tan(theta / 2) / tanHalfLens;
            double len = Math.Sqrt(dx * dx + dy * dy);
            ix = 0.5 + (len > 1e-9 ? dx / len : 0) * rn * 0.5;
            iy = 0.5 + (len > 1e-9 ? dy / len : 0) * rn * 0.5;
            return true;
        }
    }

    /// <summary>Where each output pixel of one view samples the fisheye image (fractions 0..1; NaN outside the lens).</summary>
    internal sealed class RenderMap
    {
        private readonly double[] _rotation;
        private readonly double _tanX, _tanY, _lensHalfFov;
        private readonly int _width, _height;

        private RenderMap(double[,] m, double tanX, double tanY, double lensHalfFov, int width, int height, float[] positions)
        {
            _rotation = Flatten(m);
            _tanX = tanX;
            _tanY = tanY;
            _lensHalfFov = lensHalfFov;
            _width = width;
            _height = height;
            Positions = positions;
        }

        public float[] Positions { get; }

        // The map is exact on a grid every GridStep pixels and interpolated between: the lens mapping is smooth, so
        // the error stays far below a pixel, and panning (a new map every frame) costs 1/64 of the trig.
        private const int GridStep = 8;

        public static RenderMap Build(double[,] m, double tanX, double tanY, double lensHalfFov, int width, int height)
        {
            double tanHalfLens = Math.Tan(lensHalfFov / 2);
            int gw = (width - 1 + GridStep - 1) / GridStep + 1, gh = (height - 1 + GridStep - 1) / GridStep + 1;
            if (gw < 2 || gh < 2) return BuildExact(m, tanX, tanY, lensHalfFov, tanHalfLens, width, height);
            var grid = new float[2 * gw * gh];
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    int i = 2 * (gy * gw + gx);
                    Exact(m, tanX, tanY, lensHalfFov, tanHalfLens, GridX(gx, width), GridX(gy, height), width, height, grid, i);
                }

            var xy = new float[2 * width * height];
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = 4 }, y =>
            {
                int gy = Math.Min(y / GridStep, gh - 2);
                int y0 = GridX(gy, height), y1 = GridX(gy + 1, height);
                float fy = y1 > y0 ? (float)(y - y0) / (y1 - y0) : 0;
                for (int x = 0; x < width; x++)
                {
                    int i = 2 * (y * width + x);
                    int gx = Math.Min(x / GridStep, gw - 2);
                    int x0 = GridX(gx, width), x1 = GridX(gx + 1, width);
                    float fx = x1 > x0 ? (float)(x - x0) / (x1 - x0) : 0;
                    int c00 = 2 * (gy * gw + gx), c10 = c00 + 2, c01 = c00 + 2 * gw, c11 = c01 + 2;
                    // At the lens edge part of the cell is outside the lens; map those pixels exactly.
                    if (float.IsNaN(grid[c00]) || float.IsNaN(grid[c10]) || float.IsNaN(grid[c01]) || float.IsNaN(grid[c11]))
                    {
                        Exact(m, tanX, tanY, lensHalfFov, tanHalfLens, x, y, width, height, xy, i);
                        continue;
                    }
                    for (int k = 0; k < 2; k++)
                    {
                        float top = grid[c00 + k] + (grid[c10 + k] - grid[c00 + k]) * fx;
                        float bottom = grid[c01 + k] + (grid[c11 + k] - grid[c01 + k]) * fx;
                        xy[i + k] = top + (bottom - top) * fy;
                    }
                }
            });
            return new RenderMap(m, tanX, tanY, lensHalfFov, width, height, xy);
        }

        private static RenderMap BuildExact(double[,] m, double tanX, double tanY, double lensHalfFov, double tanHalfLens, int width, int height)
        {
            var xy = new float[2 * width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    Exact(m, tanX, tanY, lensHalfFov, tanHalfLens, x, y, width, height, xy, 2 * (y * width + x));
            return new RenderMap(m, tanX, tanY, lensHalfFov, width, height, xy);
        }

        private static int GridX(int g, int size) => Math.Min(g * GridStep, size - 1);

        private static void Exact(double[,] m, double tanX, double tanY, double lensHalfFov, double tanHalfLens,
            int x, int y, int width, int height, float[] into, int i)
        {
            if (SnapshotRenderer.MapToSource(m, tanX, tanY, lensHalfFov, tanHalfLens, x, y, width, height, out double ix, out double iy))
            {
                into[i] = (float)ix;
                into[i + 1] = (float)iy;
            }
            else
                into[i] = into[i + 1] = float.NaN;
        }

        public bool Matches(double[,] m, double tanX, double tanY, double lensHalfFov, int width, int height)
        {
            if (width != _width || height != _height || tanX != _tanX || tanY != _tanY || lensHalfFov != _lensHalfFov) return false;
            double[] r = Flatten(m);
            for (int i = 0; i < r.Length; i++)
                if (r[i] != _rotation[i]) return false;
            return true;
        }

        private static double[] Flatten(double[,] m)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[3 * i + j] = m[i, j];
            return r;
        }
    }

    /// <summary>A CPU dewarp ready to run on a worker thread.</summary>
    internal sealed class RenderJob
    {
        private readonly SourceFrame _source;
        private readonly double[,] _rotation;
        private readonly double _tanX, _tanY, _lensHalfFov;
        private long _renderMs;
        private bool _mapReused;

        public RenderJob(SourceFrame source, double[,] rotation, double tanX, double tanY, double lensHalfFov, int width, int height, RenderMap map = null)
        {
            Map = map;
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

        /// <summary>The pixel map used, which the next render of the same view can reuse.</summary>
        public RenderMap Map { get; private set; }

        public BitmapSource Render()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RenderMap map = Map;
            BitmapSource result = SnapshotRenderer.Render(_source, _rotation, _tanX, _tanY, _lensHalfFov, Width, Height, ref map);
            _mapReused = map == Map;
            Map = map;
            _renderMs = sw.ElapsedMilliseconds;
            return result;
        }

        public override string ToString() =>
            $"{Width}x{Height} from {_source.Width}x{_source.Height}{(_source.IsYuv ? " YUV" : "")} (render {_renderMs} ms, map {(_mapReused ? "reused" : "built")})";
    }

    internal sealed class SourceFrame
    {
        private readonly int[] _pixels;
        private readonly byte[] _luma, _cb, _cr;   // YCbCr 4:2:0 instead of _pixels, straight from the decoder
        private readonly int _chromaWidth, _chromaHeight;

        public SourceFrame(int[] pixels, int width, int height) : this(pixels, width, height, new System.Windows.Rect(0, 0, 1, 1))
        {
        }

        /// <param name="crop">The part of the full fisheye image these pixels cover, as fractions 0..1 of it.</param>
        public SourceFrame(int[] pixels, int width, int height, System.Windows.Rect crop)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
            Crop = crop;
        }

        /// <summary>A YCbCr 4:2:0 frame; chroma planes are chromaWidth x chromaHeight.</summary>
        public SourceFrame(byte[] luma, byte[] cb, byte[] cr, int width, int height, int chromaWidth, int chromaHeight, System.Windows.Rect crop)
        {
            _luma = luma;
            _cb = cb;
            _cr = cr;
            _chromaWidth = chromaWidth;
            _chromaHeight = chromaHeight;
            Width = width;
            Height = height;
            Crop = crop;
        }

        public int Width { get; }

        public int Height { get; }

        public bool IsYuv => _luma != null;

        public System.Windows.Rect Crop { get; }

        public bool IsCropped => Crop.Width < 1 || Crop.Height < 1;

        /// <summary>Width the full fisheye image would have at this frame's pixel density.</summary>
        public double FullWidth => Width / Crop.Width;

        public double FullHeight => Height / Crop.Height;

        /// <summary>
        /// Bilinear sample at a position given as fractions 0..1 of the full fisheye image. Transparent outside a
        /// cropped frame, so the shader view underneath shows through until a frame covering that part arrives.
        /// </summary>
        public int SampleAt(double ix, double iy)
        {
            double x = (ix - Crop.X) / Crop.Width * Width - 0.5;
            double y = (iy - Crop.Y) / Crop.Height * Height - 0.5;
            if (IsCropped && (x < -1 || y < -1 || x > Width || y > Height)) return 0;
            return Sample(x, y);
        }

        /// <summary>Bilinear sample at pixel coordinates, clamped to the frame edges.</summary>
        public int Sample(double x, double y)
        {
            x = Math.Max(0, Math.Min(Width - 1.001, x));
            y = Math.Max(0, Math.Min(Height - 1.001, y));
            if (_luma != null) return SampleYuv(x, y);
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

        /// <summary>Bilinear luma and chroma, converted with BT.601 limited range (what H.264 cameras send).</summary>
        private int SampleYuv(double x, double y)
        {
            double luma = Bilinear(_luma, Width, x, y);
            // Chroma sample k sits at luma position 2k + 0.5.
            double cx = Math.Max(0, Math.Min(_chromaWidth - 1.001, (x + 0.5) * _chromaWidth / Width - 0.5));
            double cy = Math.Max(0, Math.Min(_chromaHeight - 1.001, (y + 0.5) * _chromaHeight / Height - 0.5));
            double cb = Bilinear(_cb, _chromaWidth, cx, cy) - 128, cr = Bilinear(_cr, _chromaWidth, cx, cy) - 128;
            double c = (luma - 16) * 1.164;
            int r = Clamp(c + 1.596 * cr), g = Clamp(c - 0.392 * cb - 0.813 * cr), b = Clamp(c + 2.017 * cb);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        private static double Bilinear(byte[] plane, int stride, double x, double y)
        {
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            int i = y0 * stride + x0;
            double top = plane[i] * (1 - fx) + plane[i + 1] * fx;
            double bottom = plane[i + stride] * (1 - fx) + plane[i + stride + 1] * fx;
            return top * (1 - fy) + bottom * fy;
        }

        private static int Clamp(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5);
    }
}
