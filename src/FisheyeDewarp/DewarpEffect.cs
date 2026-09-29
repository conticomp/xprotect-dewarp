using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace FisheyeDewarp
{
    /// <summary>
    /// Pixel shader that turns the fisheye image Smart Client is already drawing into a virtual PTZ view.
    /// For each output pixel it builds a view ray, rotates it by the PTZ orientation, and looks up where
    /// that ray lands in the fisheye circle using the lens projection.
    /// </summary>
    internal sealed class DewarpEffect : ShaderEffect
    {
        // Registers:
        //   c0..c2  rows of the view rotation matrix (virtual camera -> lens coordinates)
        //   c3      (tan(hfov/2), tan(vfov/2), lens half-FOV in radians, projection: 0 stereographic, 1 equidistant)
        //   c4      fisheye circle (centerX, centerY, radiusX, radiusY) in normalized image coordinates
        //   c5      where the video image sits inside the effect input (x, y, w, h), normalized 0..1
        //   c6      where to draw the dewarped output (x, y, w, h), normalized 0..1
        private const string Hlsl = @"
sampler2D input : register(s0);
float4 m0 : register(c0);
float4 m1 : register(c1);
float4 m2 : register(c2);
float4 lens : register(c3);
float4 circle : register(c4);
float4 area : register(c5);
float4 outRect : register(c6);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 o = (uv - outRect.xy) / outRect.zw;
    if (o.x < 0 || o.y < 0 || o.x > 1 || o.y > 1)
        return tex2Dlod(input, float4(uv, 0, 0));

    float2 s = o * 2 - 1;
    float3 v = normalize(float3(s.x * lens.x, s.y * lens.y, 1));
    float3 d = float3(dot(m0.xyz, v), dot(m1.xyz, v), dot(m2.xyz, v));

    float theta = acos(clamp(d.z, -1, 1));
    if (theta > lens.z)
        return float4(0, 0, 0, 1);

    float rn = lens.w < 0.5 ? tan(theta * 0.5) / tan(lens.z * 0.5) : theta / lens.z;
    float len = length(d.xy);
    float2 dir = len > 1e-6 ? d.xy / len : float2(0, 0);
    float2 img = circle.xy + dir * rn * circle.zw;
    float2 src = area.xy + img * area.zw;
    return tex2Dlod(input, float4(src, 0, 0));
}";

        private static PixelShader _shader;
        private static string _shaderError;

        public static bool TryCreate(out DewarpEffect effect, out string error)
        {
            effect = null;
            error = null;
            if (_shader == null && _shaderError == null)
            {
                try
                {
                    byte[] bytecode = ShaderCompiler.CompilePixelShader(Hlsl);
                    var shader = new PixelShader();
                    shader.SetStreamSource(new MemoryStream(bytecode));
                    shader.Freeze();
                    _shader = shader;
                    Log.Info($"Dewarp shader compiled ({bytecode.Length} bytes). ps_3_0 supported: {RenderCapability.IsPixelShaderVersionSupported(3, 0)}, render tier: {RenderCapability.Tier >> 16}");
                }
                catch (Exception ex)
                {
                    _shaderError = ex.Message;
                    Log.Error("Shader compile failed", ex);
                }
            }
            if (_shader == null)
            {
                error = _shaderError;
                return false;
            }
            effect = new DewarpEffect();
            return true;
        }

        private DewarpEffect()
        {
            PixelShader = _shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(Row0Property);
            UpdateShaderValue(Row1Property);
            UpdateShaderValue(Row2Property);
            UpdateShaderValue(LensProperty);
            UpdateShaderValue(CircleProperty);
            UpdateShaderValue(AreaProperty);
            UpdateShaderValue(OutputRectProperty);
        }

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(DewarpEffect), 0);

        public static readonly DependencyProperty Row0Property = Constant("Row0", 0, new Point4D(1, 0, 0, 0));
        public static readonly DependencyProperty Row1Property = Constant("Row1", 1, new Point4D(0, 1, 0, 0));
        public static readonly DependencyProperty Row2Property = Constant("Row2", 2, new Point4D(0, 0, 1, 0));
        public static readonly DependencyProperty LensProperty = Constant("Lens", 3, new Point4D(0.8, 0.8, 91 * Math.PI / 180, 0));
        public static readonly DependencyProperty CircleProperty = Constant("Circle", 4, new Point4D(0.5, 0.5, 0.5, 0.5));
        public static readonly DependencyProperty AreaProperty = Constant("Area", 5, new Point4D(0, 0, 1, 1));
        public static readonly DependencyProperty OutputRectProperty = Constant("OutputRect", 6, new Point4D(0, 0, 1, 1));

        private static DependencyProperty Constant(string name, int register, Point4D defaultValue) =>
            DependencyProperty.Register(name, typeof(Point4D), typeof(DewarpEffect),
                new UIPropertyMetadata(defaultValue, PixelShaderConstantCallback(register)));

        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

        public void SetRotation(double[,] m)
        {
            SetValue(Row0Property, new Point4D(m[0, 0], m[0, 1], m[0, 2], 0));
            SetValue(Row1Property, new Point4D(m[1, 0], m[1, 1], m[1, 2], 0));
            SetValue(Row2Property, new Point4D(m[2, 0], m[2, 1], m[2, 2], 0));
        }

        public void SetLens(double tanHalfFovX, double tanHalfFovY, double lensHalfFovRad, LensProjection projection) =>
            SetValue(LensProperty, new Point4D(tanHalfFovX, tanHalfFovY, lensHalfFovRad, projection == LensProjection.Stereographic ? 0 : 1));

        public void SetCircle(double cx, double cy, double rx, double ry) =>
            SetValue(CircleProperty, new Point4D(cx, cy, rx, ry));

        public void SetArea(Rect normalized) =>
            SetValue(AreaProperty, new Point4D(normalized.X, normalized.Y, normalized.Width, normalized.Height));

        public void SetOutputRect(Rect normalized) =>
            SetValue(OutputRectProperty, new Point4D(normalized.X, normalized.Y, normalized.Width, normalized.Height));
    }

    internal enum LensProjection
    {
        Stereographic,
        Equidistant,
    }
}
