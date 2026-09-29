using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FisheyeDewarp
{
    /// <summary>Thrown when this Windows install has no Media Foundation (Windows N without the Media Feature Pack, or Windows Server without the Media Foundation feature).</summary>
    internal sealed class MediaFoundationUnavailableException : Exception
    {
        public MediaFoundationUnavailableException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// H.264 MP4 writer on the Media Foundation Sink Writer, which ships with Windows. It uses a GPU encoder
    /// (NVENC, Quick Sync, AMF) when one is registered and the Microsoft software encoder otherwise.
    /// Frames go in as top-down BGRA and are converted to NV12 here, which every H.264 encoder accepts.
    /// Not thread safe; use from one worker thread.
    /// </summary>
    internal sealed class Mp4H264Writer : IDisposable
    {
        private const int MfVersion = 0x00020070;
        private const int MfStartupLite = 1;

        private readonly int _width, _height;
        private IMFSinkWriter _writer;
        private int _stream;
        private bool _started;

        public Mp4H264Writer(string path, int width, int height, int fps, int bitrate, bool hardware = true)
        {
            if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("Width and height must be even for NV12.");
            _width = width;
            _height = height;

            try
            {
                Startup();
                _started = true;
                Open(path, fps, bitrate, hardware);
            }
            catch
            {
                Dispose();   // a failed constructor would otherwise leak the writer and the MFStartup reference
                throw;
            }
        }

        /// <summary>MFStartup, turning a missing Media Foundation into a clear message. Pair with Native.MFShutdown.</summary>
        internal static void Startup()
        {
            try
            {
                Check(Native.MFStartup(MfVersion, MfStartupLite), "MFStartup");
            }
            catch (DllNotFoundException ex)
            {
                throw new MediaFoundationUnavailableException(
                    "Windows Media Foundation is not installed. On Windows N editions install the Media Feature Pack; " +
                    "on Windows Server enable the 'Media Foundation' feature (Install-WindowsFeature Server-Media-Foundation).", ex);
            }
        }

        private void Open(string path, int fps, int bitrate, bool hardware)
        {
            int width = _width;

            Check(Native.MFCreateAttributes(out IMFAttributes attributes, 2), "MFCreateAttributes");
            try
            {
                attributes.SetUINT32(Guids.ReadWriteEnableHardwareTransforms, hardware ? 1 : 0);
                attributes.SetUINT32(Guids.SinkWriterDisableThrottling, 1);
                Check(Native.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer), "MFCreateSinkWriterFromURL");
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }

            IntPtr output = CreateVideoType(Guids.VideoFormatH264, fps, t =>
            {
                t.SetUINT32(Guids.MtAvgBitrate, bitrate);
                t.SetUINT32(Guids.MtMpeg2Profile, 100);   // eAVEncH264VProfile_High
            });
            try
            {
                _writer.AddStream(output, out _stream);
            }
            finally
            {
                Marshal.Release(output);
            }

            IntPtr input = CreateVideoType(Guids.VideoFormatNV12, fps, t => t.SetUINT32(Guids.MtDefaultStride, width));
            try
            {
                _writer.SetInputMediaType(_stream, input, IntPtr.Zero);
            }
            finally
            {
                Marshal.Release(input);
            }

            _writer.BeginWriting();
        }

        /// <summary>Encode one top-down BGRA frame. Times are in 100 ns units (TimeSpan ticks) from the start of the file.</summary>
        public unsafe void WriteFrame(byte[] bgra, long time, long duration)
        {
            int length = _width * _height * 3 / 2;
            Check(Native.MFCreateMemoryBuffer(length, out IMFMediaBuffer buffer), "MFCreateMemoryBuffer");
            IMFSample sample = null;
            try
            {
                buffer.Lock(out IntPtr data, out _, out _);
                try
                {
                    fixed (byte* src = bgra)
                        BgraToNv12(src, (byte*)data, _width, _height);
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.SetCurrentLength(length);

                Check(Native.MFCreateSample(out sample), "MFCreateSample");
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(duration);
                _writer.WriteSample(_stream, sample);
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
            }
        }

        /// <summary>Flush the encoder and write the MP4 index. Without this the file is unplayable.</summary>
        public void Finish()
        {
            _writer?.FinalizeWriting();
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                Marshal.ReleaseComObject(_writer);
                _writer = null;
            }
            if (_started)
            {
                Native.MFShutdown();
                _started = false;
            }
        }

        private IntPtr CreateVideoType(Guid subtype, int fps, Action<IMFAttributes> extra)
        {
            Check(Native.MFCreateMediaType(out IntPtr type), "MFCreateMediaType");
            var attributes = (IMFAttributes)Marshal.GetObjectForIUnknown(type);
            try
            {
                attributes.SetGUID(Guids.MtMajorType, Guids.MediaTypeVideo);
                attributes.SetGUID(Guids.MtSubtype, subtype);
                attributes.SetUINT32(Guids.MtInterlaceMode, 2);   // MFVideoInterlace_Progressive
                attributes.SetUINT64(Guids.MtFrameSize, ((long)_width << 32) | (uint)_height);
                attributes.SetUINT64(Guids.MtFrameRate, ((long)fps << 32) | 1);
                attributes.SetUINT64(Guids.MtPixelAspectRatio, (1L << 32) | 1);
                extra(attributes);
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }
            return type;
        }

        /// <summary>BT.709 limited-range conversion, the colour space players assume for HD H.264.</summary>
        private static unsafe void BgraToNv12(byte* src, byte* dst, int width, int height)
        {
            byte* uvPlane = dst + width * height;
            Parallel.For(0, height / 2, pair =>
            {
                for (int row = pair * 2; row < pair * 2 + 2; row++)
                {
                    byte* s = src + row * width * 4;
                    byte* y = dst + row * width;
                    for (int x = 0; x < width; x++, s += 4)
                        y[x] = (byte)((47 * s[2] + 157 * s[1] + 16 * s[0] + 128 >> 8) + 16);
                }

                byte* s0 = src + pair * 2 * width * 4;
                byte* s1 = s0 + width * 4;
                byte* uv = uvPlane + pair * width;
                for (int x = 0; x < width; x += 2)
                {
                    int i = x * 4;
                    int b = s0[i] + s0[i + 4] + s1[i] + s1[i + 4];
                    int g = s0[i + 1] + s0[i + 5] + s1[i + 1] + s1[i + 5];
                    int r = s0[i + 2] + s0[i + 6] + s1[i + 2] + s1[i + 6];
                    // Sums of four pixels, so the >> 10 also averages.
                    uv[x] = Clamp(((-26 * r - 87 * g + 112 * b + 512) >> 10) + 128);
                    uv[x + 1] = Clamp(((112 * r - 102 * g - 10 * b + 512) >> 10) + 128);
                }
            });
        }

        private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        internal static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed", hr);
        }

        internal static class Guids
        {
            public static readonly Guid MtMajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            public static readonly Guid MtSubtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            public static readonly Guid MtAvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
            public static readonly Guid MtInterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
            public static readonly Guid MtFrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
            public static readonly Guid MtFrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
            public static readonly Guid MtPixelAspectRatio = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
            public static readonly Guid MtMpeg2Profile = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
            public static readonly Guid MtDefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
            public static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00aa00389b71");
            public static readonly Guid VideoFormatH264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
            public static readonly Guid VideoFormatNV12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");
            public static readonly Guid ReadWriteEnableHardwareTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
            public static readonly Guid SinkWriterDisableThrottling = new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        }

        internal static class Native
        {
            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFStartup(int version, int flags);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFShutdown();

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMediaType(out IntPtr mediaType);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateSample(out IMFSample sample);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

            [DllImport("mfreadwrite.dll", ExactSpelling = true)]
            public static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, out IMFSourceReader reader);

            [DllImport("mfreadwrite.dll", ExactSpelling = true)]
            public static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);
        }
    }

    /// <summary>Concatenates H.264 MP4s from the same encoder settings into one file, without re-encoding.</summary>
    internal static class Mp4Joiner
    {
        private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
        private const int AllStreams = unchecked((int)0xFFFFFFFE);
        private const int EndOfStream = 0x2;

        /// <summary>Each part's samples are shifted by its offset (100 ns units from the start of the output).</summary>
        public static void Join(IList<(string Path, long Offset)> parts, string output)
        {
            Mp4H264Writer.Startup();
            IMFSinkWriter writer = null;
            try
            {
                int stream = -1;
                foreach ((string path, long offset) in parts)
                {
                    Mp4H264Writer.Check(Mp4H264Writer.Native.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out IMFSourceReader reader), "MFCreateSourceReaderFromURL");
                    try
                    {
                        reader.SetStreamSelection(AllStreams, false);
                        reader.SetStreamSelection(FirstVideoStream, true);
                        if (writer == null)
                        {
                            Mp4H264Writer.Check(Mp4H264Writer.Native.MFCreateAttributes(out IMFAttributes attributes, 1), "MFCreateAttributes");
                            try
                            {
                                attributes.SetUINT32(Mp4H264Writer.Guids.SinkWriterDisableThrottling, 1);
                                Mp4H264Writer.Check(Mp4H264Writer.Native.MFCreateSinkWriterFromURL(output, IntPtr.Zero, attributes, out writer), "MFCreateSinkWriterFromURL");
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(attributes);
                            }
                            reader.GetCurrentMediaType(FirstVideoStream, out IntPtr type);
                            try
                            {
                                writer.AddStream(type, out stream);
                                writer.SetInputMediaType(stream, type, IntPtr.Zero);   // same type in and out: no transcoding
                            }
                            finally
                            {
                                Marshal.Release(type);
                            }
                            writer.BeginWriting();
                        }

                        while (true)
                        {
                            reader.ReadSample(FirstVideoStream, 0, out _, out int flags, out long time, out IMFSample sample);
                            if (sample != null)
                            {
                                try
                                {
                                    sample.SetSampleTime(time + offset);
                                    writer.WriteSample(stream, sample);
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(sample);
                                }
                            }
                            if ((flags & EndOfStream) != 0) break;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(reader);
                    }
                }
                writer?.FinalizeWriting();
            }
            finally
            {
                if (writer != null) Marshal.ReleaseComObject(writer);
                Mp4H264Writer.Native.MFShutdown();
            }
        }
    }

    // Minimal Media Foundation COM declarations. Only the vtable order matters; unused methods keep their
    // slot with loose signatures and are never called.

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem();
        void DeleteAllItems();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void SetDouble();
        void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        // SetString, SetBlob, SetUnknown, LockStore, UnlockStore, GetCount, GetItemByIndex, CopyAllItems follow; unused.
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        // The 30 IMFAttributes slots it inherits.
        void A00(); void A01(); void A02(); void A03(); void A04(); void A05(); void A06(); void A07(); void A08(); void A09();
        void A10(); void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19();
        void A20(); void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29();

        void GetSampleFlags(); void SetSampleFlags();
        void GetSampleTime(out long time);
        void SetSampleTime(long time);
        void GetSampleDuration();
        void SetSampleDuration(long duration);
        void GetBufferCount(); void GetBufferByIndex(); void ConvertToContiguousBuffer();
        void AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        void Unlock();
        void GetCurrentLength(out int length);
        void SetCurrentLength(int length);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        void GetStreamSelection();
        void SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType();
        void GetCurrentMediaType(int streamIndex, out IntPtr mediaType);
        void SetCurrentMediaType(); void SetCurrentPosition();
        void ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample sample);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        void AddStream(IntPtr mediaType, out int streamIndex);
        void SetInputMediaType(int streamIndex, IntPtr mediaType, IntPtr encodingParameters);
        void BeginWriting();
        void WriteSample(int streamIndex, IMFSample sample);
        void SendStreamTick(); void PlaceMarker(); void NotifyEndOfSegment(); void Flush();
        void FinalizeWriting();
    }
}
