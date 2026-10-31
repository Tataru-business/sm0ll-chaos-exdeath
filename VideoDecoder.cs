using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace DMUModelScale;

/// <summary>The media time the video should show, in seconds.</summary>
internal interface IVideoClock
{
    double Now();

    /// <summary>Called when the video reaches its end before the clock wraps.</summary>
    void Ended();
}

internal delegate void FrameUpload(ReadOnlySpan<byte> bgra, int width, int height);

/// <summary>
/// Decodes a video file with Windows Media Foundation on a background thread.
/// Frames are converted to full-resolution BGRA and released when the clock reaches them.
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    // MF_SOURCE_READER_CONSTANTS and MF_SOURCE_READER_FLAG values from mfreadwrite.h.
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint FlagError = 0x1;
    private const uint FlagEndOfStream = 0x2;
    private const uint FlagCurrentMediaTypeChanged = 0x20;
    private const ushort VtI8 = 20;
    private const uint MediaFoundationVersion = 0x00020070; // MF_VERSION

    // Attribute and format GUIDs from mfapi.h, mfidl.h and mfreadwrite.h.
    private static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");

    private readonly string path;
    private readonly IVideoClock clock;
    private readonly Thread thread;
    private readonly object frameLock = new();
    private volatile bool stopping;
    private volatile string status = "Loading video...";

    // Decoder-thread state.
    private int width;
    private int height;
    private int defaultStride;
    private double frameDuration = 1.0 / 30.0;
    private byte[] back = Array.Empty<byte>();
    private int backWidth;
    private int backHeight;

    // Shared with the render thread under frameLock.
    private byte[] ready = Array.Empty<byte>();
    private int readyWidth;
    private int readyHeight;
    private long readySerial;

    public VideoDecoder(string path, IVideoClock clock)
    {
        this.path = path;
        this.clock = clock;
        thread = new Thread(Run) { IsBackground = true, Name = "DMU floor video", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public string Status => status;

    /// <summary>Length of the video in seconds, or 0 if the file does not say.</summary>
    public double Duration { get; private set; }

    /// <summary>Hands the newest decoded frame to <paramref name="upload"/> if it is newer than <paramref name="serial"/>.</summary>
    public bool TryUploadLatest(ref long serial, FrameUpload upload)
    {
        lock (frameLock)
        {
            if (readySerial == serial || readyWidth == 0)
                return false;
            upload(ready.AsSpan(0, readyWidth * readyHeight * 4), readyWidth, readyHeight);
            serial = readySerial;
            return true;
        }
    }

    private void Run()
    {
        var comResult = CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);
        var mfStarted = false;
        IMFSourceReader* reader = null;
        try
        {
            Check(MFStartup(MediaFoundationVersion, MFSTARTUP_FULL), "start Media Foundation");
            mfStarted = true;
            reader = OpenReader();
            ReadFormat(reader);
            ReadDuration(reader);
            status = $"Playing {width}x{height} video at {1.0 / frameDuration:0.##} fps on the floor.";
            Loop(reader);
        }
        catch (Exception ex)
        {
            if (!stopping)
                status = $"Floor video could not play: {ex.Message}";
        }
        finally
        {
            if (reader != null)
                reader->Release();
            if (mfStarted)
                MFShutdown();
            if (comResult.SUCCEEDED)
                CoUninitialize();
        }
    }

    private IMFSourceReader* OpenReader()
    {
        IMFAttributes* attributes = null;
        IMFSourceReader* reader = null;
        IMFMediaType* type = null;
        try
        {
            Check(MFCreateAttributes(&attributes, 1), "create reader settings");
            // The video processor converts any decoded format (NV12, P010, ...) to RGB32 with proper colour conversion.
            var advanced = AdvancedVideoProcessing;
            Check(attributes->SetUINT32(&advanced, 1), "enable video processing");

            fixed (char* file = path)
                Check(MFCreateSourceReaderFromURL(file, attributes, &reader), "open the video file");

            Check(reader->SetStreamSelection(AllStreams, false), "select streams");
            Check(reader->SetStreamSelection(FirstVideoStream, true), "find a video stream");

            Check(MFCreateMediaType(&type), "create output format");
            var majorKey = MajorType;
            var majorValue = VideoMajorType;
            var subKey = SubType;
            var subValue = Rgb32;
            Check(type->SetGUID(&majorKey, &majorValue), "set output format");
            Check(type->SetGUID(&subKey, &subValue), "set output format");
            Check(reader->SetCurrentMediaType(FirstVideoStream, null, type), "convert the video to RGB");

            var result = reader;
            reader = null;
            return result;
        }
        finally
        {
            if (type != null)
                type->Release();
            if (reader != null)
                reader->Release();
            if (attributes != null)
                attributes->Release();
        }
    }

    private void ReadFormat(IMFSourceReader* reader)
    {
        IMFMediaType* type = null;
        Check(reader->GetCurrentMediaType(FirstVideoStream, &type), "read the video format");
        try
        {
            var sizeKey = FrameSize;
            ulong size;
            Check(type->GetUINT64(&sizeKey, &size), "read the video size");
            width = (int)(size >> 32);
            height = (int)(size & 0xFFFFFFFF);
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                throw new InvalidOperationException($"Unsupported video size {width}x{height}.");

            var rateKey = FrameRate;
            ulong rate;
            if (type->GetUINT64(&rateKey, &rate).SUCCEEDED)
            {
                var numerator = (double)(rate >> 32);
                var denominator = (double)(rate & 0xFFFFFFFF);
                if (numerator > 0 && denominator > 0)
                    frameDuration = Math.Clamp(denominator / numerator, 1.0 / 240.0, 1.0);
            }

            var strideKey = DefaultStride;
            uint stride;
            defaultStride = type->GetUINT32(&strideKey, &stride).SUCCEEDED ? unchecked((int)stride) : width * 4;
        }
        finally
        {
            type->Release();
        }
    }

    private void ReadDuration(IMFSourceReader* reader)
    {
        PROPVARIANT value = default;
        var key = PresentationDuration;
        if (reader->GetPresentationAttribute(MediaSource, &key, &value).SUCCEEDED)
            Duration = value.uhVal.QuadPart / 10_000_000.0;
        PropVariantClear(&value);
    }

    private void Loop(IMFSourceReader* reader)
    {
        var presented = -1.0;
        var endOfStream = false;
        var catchingUp = false;
        while (!stopping)
        {
            var now = clock.Now();

            // The clock went back: the music looped, or the clock source changed.
            if (presented >= 0 && now < presented - 0.5)
            {
                Seek(reader, now);
                presented = -1;
                endOfStream = false;
                catchingUp = true;
                continue;
            }

            if (endOfStream)
            {
                Thread.Sleep(5);
                continue;
            }

            uint streamIndex;
            uint flags;
            long timestamp;
            IMFSample* sample = null;
            Check(reader->ReadSample(FirstVideoStream, 0, &streamIndex, &flags, &timestamp, &sample), "read a frame");
            try
            {
                if ((flags & FlagError) != 0)
                    throw new InvalidOperationException("The video stream reported an error.");
                if ((flags & FlagCurrentMediaTypeChanged) != 0)
                    ReadFormat(reader);
                if ((flags & FlagEndOfStream) != 0)
                {
                    endOfStream = true;
                    clock.Ended();
                    continue;
                }
                if (sample == null)
                    continue;

                var time = timestamp / 10_000_000.0;
                now = clock.Now();

                // Far behind (for example after a hitch): jump ahead instead of decoding every frame.
                if (!catchingUp && time < now - 2.0)
                {
                    Seek(reader, now);
                    catchingUp = true;
                    continue;
                }

                // Late frames are skipped without copying.
                if (time + frameDuration < now)
                    continue;

                CopyToBack(sample);

                while (!stopping)
                {
                    now = clock.Now();
                    if (time <= now + 0.002 || now + 1.0 < time)
                        break;
                    Thread.Sleep(Math.Clamp((int)((time - now) * 1000) - 1, 1, 10));
                }

                if (now + 1.0 < time)
                    continue; // The clock jumped back while waiting; the next pass seeks.

                Publish();
                presented = time;
                catchingUp = false;
            }
            finally
            {
                if (sample != null)
                    sample->Release();
            }
        }
    }

    private static void Seek(IMFSourceReader* reader, double seconds)
    {
        PROPVARIANT position = default;
        position.vt = VtI8;
        position.hVal.QuadPart = (long)(Math.Max(0, seconds) * 10_000_000.0);
        var timeFormat = Guid.Empty;
        Check(reader->SetCurrentPosition(&timeFormat, &position), "seek the video");
    }

    private void CopyToBack(IMFSample* sample)
    {
        IMFMediaBuffer* buffer = null;
        Check(sample->ConvertToContiguousBuffer(&buffer), "read frame data");
        try
        {
            var size = width * height * 4;
            if (back.Length < size)
                back = new byte[size];
            backWidth = width;
            backHeight = height;

            IMF2DBuffer* buffer2D = null;
            if (buffer->QueryInterface(__uuidof<IMF2DBuffer>(), (void**)&buffer2D).SUCCEEDED)
            {
                try
                {
                    byte* scan0;
                    int pitch;
                    Check(buffer2D->Lock2D(&scan0, &pitch), "lock frame data");
                    try
                    {
                        CopyRows(scan0, pitch);
                    }
                    finally
                    {
                        buffer2D->Unlock2D();
                    }
                }
                finally
                {
                    buffer2D->Release();
                }
                return;
            }

            byte* data;
            uint maxLength;
            uint length;
            Check(buffer->Lock(&data, &maxLength, &length), "lock frame data");
            try
            {
                var stride = defaultStride == 0 ? width * 4 : defaultStride;
                if (Math.Abs((long)stride) < width * 4L || length < Math.Abs((long)stride) * height)
                    throw new InvalidOperationException("Frame data is smaller than expected.");
                // A negative stride means the image is stored bottom-up.
                var top = stride < 0 ? data + (long)-stride * (height - 1) : data;
                CopyRows(top, stride);
            }
            finally
            {
                buffer->Unlock();
            }
        }
        finally
        {
            buffer->Release();
        }
    }

    private void CopyRows(byte* top, int pitch)
    {
        var rowBytes = width * 4;
        fixed (byte* destination = back)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(top + (long)y * pitch, destination + (long)y * rowBytes, rowBytes, rowBytes);
        }

        // RGB32 leaves the alpha byte undefined. Make every pixel opaque.
        var pixels = MemoryMarshal.Cast<byte, uint>(back.AsSpan(0, rowBytes * height));
        var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(pixels);
        var opaque = new Vector<uint>(0xFF000000u);
        for (var i = 0; i < vectors.Length; i++)
            vectors[i] |= opaque;
        for (var i = vectors.Length * Vector<uint>.Count; i < pixels.Length; i++)
            pixels[i] |= 0xFF000000u;
    }

    private void Publish()
    {
        lock (frameLock)
        {
            (back, ready) = (ready, back);
            readyWidth = backWidth;
            readyHeight = backHeight;
            readySerial++;
        }
    }

    private static void Check(HRESULT result, string action)
    {
        if (result.FAILED)
            throw new COMException($"Could not {action} (0x{result.Value:X8}).", result.Value);
    }

    public void Dispose()
    {
        stopping = true;
        if (!thread.Join(TimeSpan.FromSeconds(3)))
            status = "Floor video thread did not stop in time.";
    }
}
