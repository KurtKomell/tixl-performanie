using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ManagedBass;

namespace T3.Core.Audio;

/// <summary>
/// Taps the live BASS soundtrack stream via DSP and buffers PCM float samples for video recording.
/// </summary>
public static class LiveAudioCapture
{
    private static readonly object BufferLock = new();
    private static readonly List<byte> Buffer = new(1024 * 1024);
    private static DSPProcedure? _dspProc;
    private static int _attachedStreamHandle;
    private static int _dspHandle;
    private static int _channels = 2;
    private static int _sampleRate = 48000;
    private static int _maxBufferBytes = 48000 * 2 * sizeof(float) * 2;
    private static bool _trimBuffer;

    public static int Channels => _channels;
    public static int SampleRate => _sampleRate;
    public static bool WasapiCaptureEnabled { get; set; }

    public static void AppendRawPcm(IntPtr buffer, int length)
    {
        if (!WasapiCaptureEnabled || length <= 0 || buffer == IntPtr.Zero)
            return;

        var chunk = new byte[length];
        Marshal.Copy(buffer, chunk, 0, length);

        lock (BufferLock)
        {
            Buffer.AddRange(chunk);
            TrimBufferIfNeeded();
        }
    }

    public static void ConfigureWasapiCapture(int channels, int sampleRate)
    {
        _channels = Math.Max(channels, 1);
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _maxBufferBytes = _sampleRate * _channels * sizeof(float) * 10;
        WasapiCaptureEnabled = true;
    }

    public static void DisableWasapiCapture() => WasapiCaptureEnabled = false;

    public static void Start()
    {
        _trimBuffer = false;
        _maxBufferBytes = _sampleRate * _channels * sizeof(float) * 600;
        lock (BufferLock)
            Buffer.Clear();
    }

    public static void Stop()
    {
        DetachDsp();
        DisableWasapiCapture();
        _trimBuffer = true;
        _maxBufferBytes = _sampleRate * _channels * sizeof(float) * 10;
        lock (BufferLock)
            Buffer.Clear();
    }

    public static bool EnsureAttached(int streamHandle, bool clearBuffer = true)
    {
        if (streamHandle == 0)
            return false;

        if (streamHandle == _attachedStreamHandle && _dspHandle != 0)
            return true;

        DetachDsp();

        if (!Bass.ChannelGetInfo(streamHandle, out var info))
            return false;

        _channels = info.Channels;
        _sampleRate = info.Frequency;

        _dspProc = DspCallback;
        _dspHandle = Bass.ChannelSetDSP(streamHandle, _dspProc, IntPtr.Zero, 0);
        if (_dspHandle == 0)
            return false;

        _attachedStreamHandle = streamHandle;
        if (clearBuffer)
        {
            lock (BufferLock)
                Buffer.Clear();
        }

        return true;
    }

    public static (byte[] Buffer, int SamplesRead) ReadSamples(int requestedSamples)
    {
        if (requestedSamples <= 0)
            return (Array.Empty<byte>(), 0);

        var byteCount = requestedSamples * _channels * sizeof(float);
        var result = new byte[byteCount];
        var bytesRead = 0;
        lock (BufferLock)
        {
            bytesRead = Math.Min(Buffer.Count, byteCount);
            if (bytesRead > 0)
            {
                Buffer.CopyTo(0, result, 0, bytesRead);
                Buffer.RemoveRange(0, bytesRead);
            }
        }

        var samplesRead = bytesRead / (_channels * sizeof(float));
        return (result, samplesRead);
    }

    private static void TrimBufferIfNeeded()
    {
        if (!_trimBuffer || Buffer.Count <= _maxBufferBytes)
            return;

        Buffer.RemoveRange(0, Buffer.Count - _maxBufferBytes);
    }

    private static void DetachDsp()
    {
        if (_attachedStreamHandle != 0 && _dspHandle != 0)
            Bass.ChannelRemoveDSP(_attachedStreamHandle, _dspHandle);

        _attachedStreamHandle = 0;
        _dspHandle = 0;
    }

    private static void DspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0 || buffer == IntPtr.Zero)
            return;

        var chunk = new byte[length];
        Marshal.Copy(buffer, chunk, 0, length);

        lock (BufferLock)
        {
            Buffer.AddRange(chunk);
            TrimBufferIfNeeded();
        }
    }
}
