using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.MediaFoundation;
using SharpDX.WIC;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.Logging;
using T3.Core.Resource;
using MF = SharpDX.MediaFoundation;
using Texture2D = T3.Core.DataTypes.Texture2D;

namespace T3.Player;

public enum RecordCodec
{
    H264 = 0,
    H265 = 1,
}

internal abstract class RecordMuxItem
{
    public double CaptureTimeSeconds { get; init; }
    public double DurationSeconds { get; init; }
}

internal sealed class RecordAudioMuxItem : RecordMuxItem
{
    public required byte[] Audio { get; init; }
}

internal sealed class RecordVideoMuxItem : RecordMuxItem
{
    public required byte[] Pixels { get; init; }
}

public sealed class RecordVideoEncoder : IDisposable
{
    private readonly RecordMp4VideoWriter _writer;
    private readonly TextureBgraReadAccess _readAccess = new();
    private string? _lastError;

    public RecordVideoEncoder(
        string filePath,
        int width,
        int height,
        RecordCodec codec,
        int bitrateBps,
        int fps,
        bool includeAudio,
        int audioChannels,
        int audioSampleRate)
    {
        FilePath = filePath;
        _writer = new RecordMp4VideoWriter(filePath, new Int2(width, height), codec, includeAudio)
        {
            Bitrate = bitrateBps,
            Framerate = fps,
            AudioChannels = audioChannels,
            AudioSampleRate = audioSampleRate,
        };
    }

    public string FilePath { get; }
    public string? LastError => _lastError;
    public int FramesWritten => _writer.FramesWritten;

    public void Update() => _readAccess.Update();

    public void PumpAudio(byte[]? audioFrame, double captureTimeSeconds, double durationSeconds)
    {
        try
        {
            var audio = audioFrame ?? Array.Empty<byte>();
            _writer.EnqueueAudio(audio, captureTimeSeconds, durationSeconds);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Warning($"Record audio pump: {ex.Message}");
        }
    }

    public bool ProcessVideoFrame(Texture2D gpuTexture, double captureTimeSeconds, double frameDurationSeconds)
    {
        try
        {
            return _writer.ProcessVideoFrame(ref gpuTexture, captureTimeSeconds, frameDurationSeconds, _readAccess, OnReadbackComplete);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Warning($"Record encoder: {ex.Message}");
            return false;
        }
    }

    private void OnReadbackComplete(TextureBgraReadAccess.ReadRequestItem readRequestItem)
    {
        try
        {
            _writer.CompleteReadback(readRequestItem);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Warning($"Record readback: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Warning($"Record encoder finalize: {ex.Message}");
        }

        _readAccess.Dispose();
    }
}

internal static class RecordMfHelper
{
    internal static Guid BuildVideoSubtypeGuid(string fourCcString)
    {
        return new Guid(
            GetFourCcValue(fourCcString),
            0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    }

    private static uint GetFourCcValue(string fourCcString)
    {
        if (string.IsNullOrEmpty(fourCcString))
            throw new ArgumentNullException(nameof(fourCcString));
        if (fourCcString.Length > 4)
            throw new ArgumentException("Given value too long!");

        var asciiBytes = System.Text.Encoding.UTF8.GetBytes(fourCcString);
        var fccValueBytes = new byte[4];
        for (var loop = 0; loop < 4; loop++)
            fccValueBytes[loop] = asciiBytes.Length > loop ? asciiBytes[loop] : (byte)0x20;

        return BitConverter.ToUInt32(fccValueBytes, 0);
    }

    internal static long GetMfEncodedIntsByValues(int valueA, int valueB)
    {
        var valueXBytes = BitConverter.GetBytes(valueA);
        var valueYBytes = BitConverter.GetBytes(valueB);
        var resultBytes = new byte[8];
        if (BitConverter.IsLittleEndian)
        {
            resultBytes[0] = valueYBytes[0];
            resultBytes[1] = valueYBytes[1];
            resultBytes[2] = valueYBytes[2];
            resultBytes[3] = valueYBytes[3];
            resultBytes[4] = valueXBytes[0];
            resultBytes[5] = valueXBytes[1];
            resultBytes[6] = valueXBytes[2];
            resultBytes[7] = valueXBytes[3];
        }
        else
        {
            resultBytes[0] = valueXBytes[0];
            resultBytes[1] = valueXBytes[1];
            resultBytes[2] = valueXBytes[2];
            resultBytes[3] = valueXBytes[3];
            resultBytes[4] = valueYBytes[0];
            resultBytes[5] = valueYBytes[1];
            resultBytes[6] = valueYBytes[2];
            resultBytes[7] = valueYBytes[3];
        }

        return BitConverter.ToInt64(resultBytes, 0);
    }
}

internal struct RecordWaveFormatExtension
{
    public SharpDX.Multimedia.WaveFormatEncoding _wFormatTag;
    public ushort _nChannels;
    public uint _nSamplesPerSec;
    public uint _nAvgBytesPerSec;
    public ushort _nBlockAlign;
    public ushort _wBitsPerSample;
    public ushort _cbSize;

    public static RecordWaveFormatExtension DefaultIeee
    {
        get
        {
            var waveFormatEx = new RecordWaveFormatExtension
            {
                _wFormatTag = SharpDX.Multimedia.WaveFormatEncoding.IeeeFloat,
                _nChannels = 2,
                _nSamplesPerSec = 48000,
                _wBitsPerSample = 32,
            };
            waveFormatEx._nBlockAlign = (ushort)(waveFormatEx._nChannels * waveFormatEx._wBitsPerSample / 8);
            waveFormatEx._nAvgBytesPerSec = waveFormatEx._nSamplesPerSec * waveFormatEx._nBlockAlign;
            waveFormatEx._cbSize = 0;
            return waveFormatEx;
        }
    }

    public SharpDX.Multimedia.WaveFormat ToSharpDx()
    {
        return SharpDX.Multimedia.WaveFormat.CreateCustomFormat(
            _wFormatTag,
            (int)_nSamplesPerSec,
            _nChannels,
            (int)_nAvgBytesPerSec,
            _nBlockAlign,
            _wBitsPerSample);
    }
}

internal sealed class RecordAacAudioWriter
{
    private readonly int _streamIndex;

    public RecordAacAudioWriter(MF.SinkWriter sinkWriter, ref RecordWaveFormatExtension waveFormat, int desiredBitRate = 192000)
    {
        var sharpWf = waveFormat.ToSharpDx();
        var outputMediaType = SelectMediaType(MF.AudioFormatGuids.Aac, sharpWf, desiredBitRate)
                              ?? throw new InvalidOperationException("No suitable AAC encoder available");

        var inputMediaType = new MF.MediaType();
        var size = 18 + sharpWf.ExtraSize;
        sinkWriter.AddStream(outputMediaType, out _streamIndex);
        MF.MediaFactory.InitMediaTypeFromWaveFormatEx(inputMediaType, new[] { sharpWf }, size);
        sinkWriter.SetInputMediaType(_streamIndex, inputMediaType, null);
    }

    public int StreamIndex => _streamIndex;

    public MF.Sample CreateSampleFromFrame(byte[] data)
    {
        var mediaBuffer = MF.MediaFactory.CreateMemoryBuffer(data.Length);
        var mediaBufferPointer = mediaBuffer.Lock(out _, out _);
        try
        {
            Marshal.Copy(data, 0, mediaBufferPointer, data.Length);
        }
        finally
        {
            mediaBuffer.Unlock();
            mediaBuffer.CurrentLength = data.Length;
        }

        var sample = MF.MediaFactory.CreateSample();
        sample.AddBuffer(mediaBuffer);
        return sample;
    }

    private static MF.MediaType? SelectMediaType(Guid audioSubtype, SharpDX.Multimedia.WaveFormat inputFormat, int desiredBitRate)
    {
        return GetOutputMediaTypes(audioSubtype)
            .Where(mt => mt.Get(MF.MediaTypeAttributeKeys.AudioSamplesPerSecond) == inputFormat.SampleRate
                         && mt.Get(MF.MediaTypeAttributeKeys.AudioNumChannels) == inputFormat.Channels)
            .Select(mt => new { MediaType = mt, Delta = Math.Abs(desiredBitRate - mt.Get(MF.MediaTypeAttributeKeys.AudioAvgBytesPerSecond) * 8) })
            .OrderBy(mt => mt.Delta)
            .Select(mt => mt.MediaType)
            .FirstOrDefault();
    }

    private static IEnumerable<MF.MediaType> GetOutputMediaTypes(Guid audioSubtype)
    {
        MF.Collection availableTypes;
        try
        {
            availableTypes = MF.MediaFactory.TranscodeGetAudioOutputAvailableTypes(audioSubtype, MF.TransformEnumFlag.All, null);
        }
        catch (SharpDXException c)
        {
            if (c.ResultCode.Code == MF.ResultCode.NotFound.Code)
                return Array.Empty<MF.MediaType>();
            throw;
        }

        var count = availableTypes.ElementCount;
        var mediaTypes = new List<MF.MediaType>(count);
        for (var n = 0; n < count; n++)
        {
            var mediaTypeObject = (ComObject)availableTypes.GetElement(n);
            mediaTypes.Add(new MF.MediaType(mediaTypeObject.NativePointer));
        }

        availableTypes.Dispose();
        return mediaTypes;
    }
}

internal sealed class RecordMp4VideoWriter : IDisposable
{
    private const int MaxQueuedItems = 120;

    private static readonly Guid VideoInputFormatId = VideoFormatGuids.Rgb32;
    private static bool _mfInitialized;

    private readonly string _filePath;
    private readonly Int2 _videoPixelSize;
    private readonly RecordCodec _codec;
    private readonly bool _supportAudio;
    private readonly Guid _videoInputFormat = VideoInputFormatId;
    private readonly BlockingCollection<RecordMuxItem> _muxQueue = new(MaxQueuedItems);
    private readonly ConcurrentBag<byte[]> _pixelBufferPool = new();
    private readonly ConcurrentQueue<(double CaptureTimeSec, double FrameDurationSec)> _pendingVideoTiming = new();
    private readonly Thread _workerThread;
    private readonly object _writerInitLock = new();

    private MF.SinkWriter? _sinkWriter;
    private RecordAacAudioWriter? _audioWriter;
    private int _streamIndex;
    private int _videoFramesWritten;
    private bool _writerInitialized;
    private int _initChannels = 2;
    private int _initSampleRate = 48000;
    private volatile bool _disposed;

    public RecordMp4VideoWriter(string filePath, Int2 videoPixelSize, RecordCodec codec, bool supportAudio)
    {
        EnsureMediaFoundationStarted();
        _filePath = filePath;
        _videoPixelSize = videoPixelSize;
        _codec = codec;
        _supportAudio = supportAudio;
        Bitrate = 2_000_000;
        Framerate = 60;
        AudioChannels = 2;
        AudioSampleRate = 48000;

        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RecordEncoderWorker",
        };
        _workerThread.Start();
    }

    public int Bitrate { get; set; }
    public int Framerate { get; set; }
    public int AudioChannels { get; set; }
    public int AudioSampleRate { get; set; }
    public int FramesWritten => Volatile.Read(ref _videoFramesWritten);

    public void EnqueueAudio(byte[] audioFrame, double captureTimeSeconds, double durationSeconds)
    {
        if (_disposed || !_supportAudio || audioFrame.Length == 0)
            return;

        _muxQueue.Add(new RecordAudioMuxItem
        {
            Audio = audioFrame,
            CaptureTimeSeconds = captureTimeSeconds,
            DurationSeconds = durationSeconds,
        });
    }

    public bool ProcessVideoFrame(
        ref Texture2D gpuTexture,
        double captureTimeSeconds,
        double frameDurationSeconds,
        TextureBgraReadAccess readAccess,
        TextureBgraReadAccess.OnReadComplete onReadComplete)
    {
        if (_disposed)
            return false;

        _pendingVideoTiming.Enqueue((captureTimeSeconds, frameDurationSeconds));

        if (!readAccess.InitiateConvertAndReadBack(gpuTexture, onReadComplete))
        {
            _pendingVideoTiming.TryDequeue(out _);
            Log.Warning("Record: can't initiate texture readback");
            return false;
        }

        return true;
    }

    public void CompleteReadback(TextureBgraReadAccess.ReadRequestItem readRequestItem)
    {
        if (_disposed)
            return;

        var cpuAccessTexture = readRequestItem.CpuAccessTexture;
        if (cpuAccessTexture == null || cpuAccessTexture.IsDisposed)
            return;

        if (!_pendingVideoTiming.TryDequeue(out var timing))
        {
            Log.Warning("Record: readback without matching video timing");
            timing = (0, 1.0 / Math.Max(Framerate, 1));
        }

        var width = cpuAccessTexture.Description.Width;
        var height = cpuAccessTexture.Description.Height;
        var rowStride = PixelFormat.GetStride(PixelFormat.Format32bppRGBA, width);
        var pixelBytes = RgbaSizeInBytes(width, height);
        var pixels = RentPixelBuffer(pixelBytes);

        var context = ResourceManager.Device.ImmediateContext;
        var dataBox = context.MapSubresource(
            cpuAccessTexture,
            0,
            0,
            SharpDX.Direct3D11.MapMode.Read,
            SharpDX.Direct3D11.MapFlags.None,
            out var inputStream);

        try
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var dstBase = handle.AddrOfPinnedObject();
                var copyBytes = Math.Min(rowStride, (int)dataBox.RowPitch);
                for (var y = 0; y < height; y++)
                {
                    var srcRow = IntPtr.Add(dataBox.DataPointer, (int)((height - 1 - y) * dataBox.RowPitch));
                    var dstRow = IntPtr.Add(dstBase, y * rowStride);
                    Utilities.CopyMemory(dstRow, srcRow, copyBytes);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception e)
        {
            ReturnPixelBuffer(pixels);
            Log.Error("Record: failed to read video frame: " + e.Message);
            return;
        }
        finally
        {
            inputStream?.Dispose();
            context.UnmapSubresource(cpuAccessTexture, 0);
        }

        _muxQueue.Add(new RecordVideoMuxItem
        {
            Pixels = pixels,
            CaptureTimeSeconds = timing.CaptureTimeSec,
            DurationSeconds = timing.FrameDurationSec,
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _muxQueue.CompleteAdding();

        try
        {
            if (!_workerThread.Join(TimeSpan.FromSeconds(30)))
                Log.Warning("Record: encoder worker did not finish in time");
        }
        catch (Exception ex)
        {
            Log.Warning($"Record: worker join failed: {ex.Message}");
        }

        if (_sinkWriter == null)
            return;

        try
        {
            _sinkWriter.NotifyEndOfSegment(_streamIndex);
            if (_videoFramesWritten > 0)
                _sinkWriter.Finalize();
        }
        finally
        {
            _sinkWriter.Dispose();
            _sinkWriter = null;
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var item in _muxQueue.GetConsumingEnumerable())
            {
                try
                {
                    switch (item)
                    {
                        case RecordAudioMuxItem audio:
                            EncodeAudioOnWorker(audio);
                            break;
                        case RecordVideoMuxItem video:
                            EncodeVideoOnWorker(video);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Record worker: {ex.Message}");
                }
                finally
                {
                    if (item is RecordVideoMuxItem videoItem)
                        ReturnPixelBuffer(videoItem.Pixels);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Record worker loop ended: {ex.Message}");
        }
    }

    private void EncodeAudioOnWorker(RecordAudioMuxItem item)
    {
        EnsureWriterInitialized();
        if (_audioWriter == null)
            return;

        var audioSample = _audioWriter.CreateSampleFromFrame(item.Audio);
        try
        {
            WriteSample(_audioWriter.StreamIndex, audioSample, item.CaptureTimeSeconds, item.DurationSeconds);
        }
        finally
        {
            audioSample.Dispose();
        }
    }

    private void EncodeVideoOnWorker(RecordVideoMuxItem item)
    {
        EnsureWriterInitialized();

        var videoSample = CreateVideoSampleFromPixels(item.Pixels);
        try
        {
            WriteSample(_streamIndex, videoSample, item.CaptureTimeSeconds, item.DurationSeconds);
            Interlocked.Increment(ref _videoFramesWritten);
        }
        finally
        {
            videoSample.Dispose();
        }
    }

    private MF.Sample CreateVideoSampleFromPixels(byte[] pixels)
    {
        var mediaBufferLength = pixels.Length;
        var mediaBuffer = MF.MediaFactory.CreateMemoryBuffer(mediaBufferLength);
        var mediaBufferPointer = mediaBuffer.Lock(out _, out _);
        try
        {
            Marshal.Copy(pixels, 0, mediaBufferPointer, mediaBufferLength);
        }
        finally
        {
            mediaBuffer.Unlock();
            mediaBuffer.CurrentLength = mediaBufferLength;
        }

        var sample = MF.MediaFactory.CreateSample();
        sample.AddBuffer(mediaBuffer);
        mediaBuffer.Dispose();
        return sample;
    }

    private byte[] RentPixelBuffer(int size)
    {
        if (_pixelBufferPool.TryTake(out var buffer) && buffer.Length >= size)
            return buffer;

        return new byte[size];
    }

    private void ReturnPixelBuffer(byte[] buffer)
    {
        if (buffer.Length > 0)
            _pixelBufferPool.Add(buffer);
    }

    private void EnsureWriterInitialized()
    {
        if (_writerInitialized)
            return;

        lock (_writerInitLock)
        {
            if (_writerInitialized)
                return;

            _sinkWriter = CreateSinkWriter(_filePath);
            CreateMediaTarget(_sinkWriter, _videoPixelSize, out _streamIndex);

            using (var mediaTypeIn = new MF.MediaType())
            {
                mediaTypeIn.Set(MF.MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                mediaTypeIn.Set(MF.MediaTypeAttributeKeys.Subtype, _videoInputFormat);
                mediaTypeIn.Set(MF.MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                mediaTypeIn.Set(MF.MediaTypeAttributeKeys.FrameSize, RecordMfHelper.GetMfEncodedIntsByValues(_videoPixelSize.Width, _videoPixelSize.Height));
                mediaTypeIn.Set(MF.MediaTypeAttributeKeys.FrameRate, RecordMfHelper.GetMfEncodedIntsByValues(Framerate, 1));
                _sinkWriter.SetInputMediaType(_streamIndex, mediaTypeIn, null);
            }

            if (_supportAudio)
            {
                var waveFormat = RecordWaveFormatExtension.DefaultIeee;
                waveFormat._nChannels = (ushort)AudioChannels;
                waveFormat._nSamplesPerSec = (uint)AudioSampleRate;
                waveFormat._nBlockAlign = (ushort)(waveFormat._nChannels * waveFormat._wBitsPerSample / 8);
                waveFormat._nAvgBytesPerSec = waveFormat._nSamplesPerSec * waveFormat._nBlockAlign;
                _audioWriter = new RecordAacAudioWriter(_sinkWriter, ref waveFormat);
            }

            _sinkWriter.BeginWriting();
            _writerInitialized = true;
        }
    }

    public void SetAudioFormat(int channels, int sampleRate)
    {
        _initChannels = channels;
        _initSampleRate = sampleRate;
    }

    private void CreateMediaTarget(MF.SinkWriter sinkWriter, Int2 videoPixelSize, out int streamIndex)
    {
        using var mediaTypeOut = new MF.MediaType();
        mediaTypeOut.Set(MF.MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaTypeOut.Set(MF.MediaTypeAttributeKeys.Subtype, GetCodecGuid(_codec));
        mediaTypeOut.Set(MF.MediaTypeAttributeKeys.AvgBitrate, Bitrate);
        mediaTypeOut.Set(MF.MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
        mediaTypeOut.Set(MF.MediaTypeAttributeKeys.FrameSize, RecordMfHelper.GetMfEncodedIntsByValues(videoPixelSize.Width, videoPixelSize.Height));
        mediaTypeOut.Set(MF.MediaTypeAttributeKeys.FrameRate, RecordMfHelper.GetMfEncodedIntsByValues(Framerate, 1));
        sinkWriter.AddStream(mediaTypeOut, out streamIndex);
    }

    private static Guid GetCodecGuid(RecordCodec codec) => codec switch
    {
        RecordCodec.H265 => RecordMfHelper.BuildVideoSubtypeGuid("HEVC"),
        _ => VideoFormatGuids.H264,
    };

    private static MF.SinkWriter CreateSinkWriter(string outputFile)
    {
        using var attributes = new MF.MediaAttributes();
        MF.MediaFactory.CreateAttributes(attributes, 1);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms.Guid, (uint)1);
        try
        {
            return MF.MediaFactory.CreateSinkWriterFromURL(outputFile, null, attributes);
        }
        catch (COMException e)
        {
            if (e.ErrorCode == unchecked((int)0xC00D36D5))
                throw new ArgumentException("Was not able to create a sink writer for this file extension");
            throw;
        }
    }

    private void WriteSample(int streamIndex, MF.Sample sample, double captureTimeSeconds, double durationSeconds)
    {
        var sampleTime = (long)(Math.Max(0, captureTimeSeconds) * 10_000_000);
        var sampleDuration = Math.Max(1L, (long)(Math.Max(1.0 / 6000.0, durationSeconds) * 10_000_000));
        sample.SampleTime = sampleTime;
        sample.SampleDuration = sampleDuration;
        _sinkWriter!.WriteSample(streamIndex, sample);
    }

    private static int RgbaSizeInBytes(int width, int height) => (width * height * 32 + 7) / 8;

    private static void EnsureMediaFoundationStarted()
    {
        if (_mfInitialized)
            return;
        MF.MediaFactory.Startup(MF.MediaFactory.Version, 0);
        _mfInitialized = true;
    }
}
