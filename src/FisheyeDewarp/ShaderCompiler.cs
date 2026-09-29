using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FisheyeDewarp
{
    /// <summary>
    /// Compiles HLSL to ps_3_0 bytecode at runtime with d3dcompiler_47.dll, which ships with Windows 10/11.
    /// Avoids needing the Windows SDK's fxc.exe at build time.
    /// </summary>
    internal static class ShaderCompiler
    {
        [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] UIntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DCompile(
            byte[] srcData, UIntPtr srcDataSize, string sourceName, IntPtr defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target,
            uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errorMsgs);

        private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

        public static byte[] CompilePixelShader(string hlsl, string entryPoint = "main", string target = "ps_3_0")
        {
            byte[] src = Encoding.ASCII.GetBytes(hlsl);
            int hr = D3DCompile(src, (UIntPtr)src.Length, "dewarp.hlsl", IntPtr.Zero, IntPtr.Zero,
                entryPoint, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out ID3DBlob code, out ID3DBlob errors);
            try
            {
                if (hr < 0)
                {
                    string message = errors == null ? "" : Marshal.PtrToStringAnsi(errors.GetBufferPointer());
                    throw new InvalidOperationException($"D3DCompile failed (0x{hr:X8}): {message}");
                }
                int size = (int)code.GetBufferSize();
                byte[] bytecode = new byte[size];
                Marshal.Copy(code.GetBufferPointer(), bytecode, 0, size);
                return bytecode;
            }
            finally
            {
                if (code != null) Marshal.ReleaseComObject(code);
                if (errors != null) Marshal.ReleaseComObject(errors);
            }
        }
    }
}
