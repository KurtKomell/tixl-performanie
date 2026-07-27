using System;
using System.IO;
using ManagedBass;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Plays a standalone audio file for Record Edition capture via <see cref="LiveAudioCapture"/>.
/// </summary>
public static class RecordAudioPlayback
{
    private const double EndToleranceSeconds = 0.25;

    private static string? _filePath;
    private static int _streamHandle;
    private static double _durationSeconds;
    private static bool _loopEnabled;

    public static string? FilePath => _filePath;
    public static double DurationSeconds => _durationSeconds;
    public static bool LoopEnabled => _loopEnabled;
    public static bool HasFile => !string.IsNullOrEmpty(_filePath) && File.Exists(_filePath);
    public static bool IsActive => _streamHandle != 0 && Bass.ChannelIsActive(_streamHandle) == PlaybackState.Playing;

    public static void SetFilePath(string? absolutePath)
    {
        if (string.Equals(_filePath, absolutePath, StringComparison.OrdinalIgnoreCase))
            return;

        Stop();
        _filePath = string.IsNullOrWhiteSpace(absolutePath) ? null : absolutePath;
        _durationSeconds = 0;
    }

    public static void SetLoopEnabled(bool enabled)
    {
        _loopEnabled = enabled;
        if (_streamHandle != 0)
            Bass.ChannelFlags(_streamHandle, enabled ? BassFlags.Loop : BassFlags.Default, BassFlags.Loop);
    }

    public static bool TryProbeDuration(out double durationSeconds)
    {
        durationSeconds = 0;
        if (!HasFile)
            return false;

        durationSeconds = ProbeFileDurationSeconds(_filePath!);
        if (durationSeconds > 0)
            _durationSeconds = durationSeconds;

        return durationSeconds > 0;
    }

    public static bool TryStartForRecording(out int streamHandle, out int channels, out int sampleRate)
    {
        if (!TryPrepareStream(out streamHandle, out channels, out sampleRate))
            return false;

        StartPlayback();
        return true;
    }

    public static bool TryPrepareStream(out int streamHandle, out int channels, out int sampleRate)
    {
        streamHandle = 0;
        channels = 2;
        sampleRate = 48000;

        if (!HasFile)
            return false;

        Stop();

        if (!Bass.Init() && Bass.LastError != Errors.Already)
        {
            Log.Warning($"Record audio: BASS init failed ({Bass.LastError})");
            return false;
        }

        var streamFlags = BassFlags.Prescan | BassFlags.Float;
        if (_loopEnabled)
            streamFlags |= BassFlags.Loop;

        _streamHandle = Bass.CreateStream(_filePath!, 0, 0, streamFlags);
        if (_streamHandle == 0)
        {
            Log.Warning($"Record audio: failed to load '{_filePath}' ({Bass.LastError})");
            return false;
        }

        _durationSeconds = GetExpectedDurationSeconds();
        Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume, 1);
        Bass.ChannelSetPosition(_streamHandle, 0);

        if (!Bass.ChannelGetInfo(_streamHandle, out var info))
        {
            Stop();
            return false;
        }

        channels = info.Channels;
        sampleRate = info.Frequency;
        streamHandle = _streamHandle;
        return true;
    }

    public static void StartPlayback()
    {
        if (_streamHandle == 0)
            return;

        Bass.ChannelSetPosition(_streamHandle, 0);
        if (!Bass.ChannelPlay(_streamHandle))
            Log.Warning($"Record audio: playback failed ({Bass.LastError})");
    }

    /// <summary>
    /// Keeps playback running until the probed file duration is reached.
    /// Some codecs report an early BASS stop before the real end of the file.
    /// </summary>
    public static void MaintainPlaybackDuringRecording()
    {
        if (_streamHandle == 0)
            return;

        var positionSeconds = GetPositionSeconds();
        var expectedDuration = GetExpectedDurationSeconds();
        if (positionSeconds >= expectedDuration - EndToleranceSeconds)
            return;

        var state = Bass.ChannelIsActive(_streamHandle);
        if (state == PlaybackState.Playing || state == PlaybackState.Stalled)
            return;

        if (!Bass.ChannelPlay(_streamHandle, false))
            Log.Warning($"Record audio: resume failed ({Bass.LastError}) at {positionSeconds:0.00}s");
    }

    public static (double PositionSec, double ExpectedDurationSec, string BassState) GetPlaybackDiagnostics()
    {
        if (_streamHandle == 0)
            return (0, 0, "none");

        return (GetPositionSeconds(), GetExpectedDurationSeconds(), Bass.ChannelIsActive(_streamHandle).ToString());
    }

    public static double GetPlaybackPositionSeconds() => GetPositionSeconds();

    public static bool ShouldStopRecording()
    {
        if (_streamHandle == 0)
            return false;

        var positionSeconds = GetPositionSeconds();
        var expectedDuration = GetExpectedDurationSeconds();
        if (positionSeconds < expectedDuration - EndToleranceSeconds)
            return false;

        var state = Bass.ChannelIsActive(_streamHandle);
        return state != PlaybackState.Playing && state != PlaybackState.Stalled;
    }

    public static void Stop()
    {
        if (_streamHandle == 0)
            return;

        Bass.ChannelStop(_streamHandle);
        Bass.StreamFree(_streamHandle);
        _streamHandle = 0;
    }

    private static double GetPositionSeconds()
    {
        if (_streamHandle == 0)
            return 0;

        var position = Bass.ChannelGetPosition(_streamHandle);
        if (position < 0)
            return 0;

        return Bass.ChannelBytes2Seconds(_streamHandle, position);
    }

    private static double GetExpectedDurationSeconds()
    {
        var expected = _durationSeconds;

        if (_streamHandle != 0)
        {
            var length = Bass.ChannelGetLength(_streamHandle);
            if (length > 0)
                expected = Math.Max(expected, Bass.ChannelBytes2Seconds(_streamHandle, length));
        }

        if (expected > 0)
            return expected;

        if (!string.IsNullOrEmpty(_filePath))
            return ProbeFileDurationSeconds(_filePath);

        return 0;
    }

    private static double ProbeFileDurationSeconds(string path)
    {
        var probeHandle = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Prescan);
        if (probeHandle == 0)
            return 0;

        try
        {
            var bytes = Bass.ChannelGetLength(probeHandle);
            if (bytes < 0)
                return 0;

            return Bass.ChannelBytes2Seconds(probeHandle, bytes);
        }
        finally
        {
            Bass.StreamFree(probeHandle);
        }
    }
}
