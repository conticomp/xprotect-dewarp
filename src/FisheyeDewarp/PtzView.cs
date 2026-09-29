using System;
using System.Windows;

namespace FisheyeDewarp
{
    internal enum MountPosition
    {
        Ceiling,
        Wall,
    }

    /// <summary>Virtual PTZ state for one tile, and the rotation it implies.</summary>
    internal sealed class PtzView
    {
        private const double Deg = Math.PI / 180;

        public MountPosition Mount { get; set; } = MountPosition.Ceiling;

        /// <summary>Ceiling: azimuth around the lens axis. Wall: yaw left/right.</summary>
        public double Pan { get; private set; }

        /// <summary>Ceiling: angle away from straight down (0 = center of the circle). Wall: pitch up/down.</summary>
        public double Tilt { get; private set; }

        /// <summary>Horizontal field of view of the dewarped output.</summary>
        public double Fov { get; private set; }

        public PtzView() => Reset();

        public void Reset()
        {
            Pan = 0;
            Tilt = Mount == MountPosition.Ceiling ? 50 * Deg : 0;
            Fov = 80 * Deg;
        }

        /// <summary>Grab-and-drag: fractions of the output width/height the pointer moved.</summary>
        public void Drag(double dxFraction, double dyFraction, double aspect)
        {
            double vfov = 2 * Math.Atan(Math.Tan(Fov / 2) / aspect);
            if (Mount == MountPosition.Ceiling)
            {
                Pan -= dxFraction * Fov / Math.Max(Math.Sin(Tilt), 0.25);
                Tilt = Clamp(Tilt + dyFraction * vfov, 0, 90 * Deg);
            }
            else
            {
                Pan = Clamp(Pan - dxFraction * Fov, -90 * Deg, 90 * Deg);
                Tilt = Clamp(Tilt + dyFraction * vfov, -85 * Deg, 85 * Deg);
            }
            Pan = Math.IEEERemainder(Pan, 2 * Math.PI);
        }

        public void Zoom(int wheelDelta)
        {
            Fov = Clamp(Fov * Math.Pow(0.9, wheelDelta / 120.0), 15 * Deg, 130 * Deg);
        }

        /// <summary>Row-major matrix mapping a virtual-camera ray (x right, y down, z forward) into lens coordinates.</summary>
        public double[,] Rotation()
        {
            // Pitch "up" (forward tilts toward -y, i.e. toward the top of the image / the horizon).
            double ct = Math.Cos(Tilt), st = Math.Sin(Tilt);
            double[,] pitch =
            {
                { 1, 0, 0 },
                { 0, ct, -st },
                { 0, st, ct },
            };
            double cp = Math.Cos(Pan), sp = Math.Sin(Pan);
            double[,] pan = Mount == MountPosition.Ceiling
                ? new double[,] { { cp, -sp, 0 }, { sp, cp, 0 }, { 0, 0, 1 } }   // around the lens axis
                : new double[,] { { cp, 0, sp }, { 0, 1, 0 }, { -sp, 0, cp } };  // around world vertical
            return Multiply(pan, pitch);
        }

        /// <summary>
        /// Smallest part of the fisheye image (normalized 0..1) that the current view samples from.
        /// Mirrors the shader math on a grid of output points.
        /// </summary>
        public Rect SourceRegion(double aspect, double lensHalfFov)
        {
            double[,] m = Rotation();
            double tanX = Math.Tan(Fov / 2), tanY = tanX / aspect;
            double minX = 1, minY = 1, maxX = 0, maxY = 0;
            const int steps = 12;
            for (int i = 0; i <= steps; i++)
            {
                for (int j = 0; j <= steps; j++)
                {
                    double sx = (i / (double)steps * 2 - 1) * tanX, sy = (j / (double)steps * 2 - 1) * tanY;
                    double n = Math.Sqrt(sx * sx + sy * sy + 1);
                    double vx = sx / n, vy = sy / n, vz = 1 / n;
                    double dx = m[0, 0] * vx + m[0, 1] * vy + m[0, 2] * vz;
                    double dy = m[1, 0] * vx + m[1, 1] * vy + m[1, 2] * vz;
                    double dz = m[2, 0] * vx + m[2, 1] * vy + m[2, 2] * vz;
                    double theta = Math.Acos(Clamp(dz, -1, 1));
                    double rn = Math.Min(Math.Tan(theta / 2) / Math.Tan(lensHalfFov / 2), 1);
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    double ix = 0.5 + (len > 1e-9 ? dx / len : 0) * rn * 0.5;
                    double iy = 0.5 + (len > 1e-9 ? dy / len : 0) * rn * 0.5;
                    minX = Math.Min(minX, ix); maxX = Math.Max(maxX, ix);
                    minY = Math.Min(minY, iy); maxY = Math.Max(maxY, iy);
                }
            }
            return new Rect(new Point(minX, minY), new Point(maxX, maxY));
        }

        public override string ToString() =>
            $"mount={Mount} pan={Pan / Deg:F1} tilt={Tilt / Deg:F1} fov={Fov / Deg:F1}";

        private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            return r;
        }
    }
}
