using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using T3.Core;
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Resource;
using ComputeShader = T3.Core.DataTypes.ComputeShader;
using Texture2D = T3.Core.DataTypes.Texture2D;

namespace T3.Player;

public partial class Program
{
    public static bool RecordEnabled;
    public static int RecordTargetWidth = 1920;
    public static int RecordTargetHeight = 1080;
    public static string RecordOutputFolder = string.Empty;
    public static RecordCodec RecordCodec = RecordCodec.H264;
    public static int RecordBitrateBps = 25_000_000;
    public static int RecordFps = 60;
    public static bool RecordIncludeAudio = true;
    public static string? RecordAudioFilePath;
    public static string? RecordStatusMessage;

    private static RecordVideoEncoder? _videoEncoder;
    private static bool _recordingUsesLoadedAudio;
    private static double _lastCaptureRunTime = -1.0;
    private static double _lastAudioPumpTime = -1.0;
    private static double _recordStartRunTime = -1.0;
    private static Resource<ComputeShader>? _resizeComputeShaderResource;
    private static Texture2D? _resizeTargetTexture;
    private static UnorderedAccessView? _resizeTargetUav;
    private static SharpDX.Direct3D11.Buffer? _resizeConstantBuffer;
    private static Texture2D[]? _recordSnapshotTextures;
    private static int _recordSnapshotIndex;

    private const int ResizeThreadGroupSize = 16;
    private const int RecordSnapshotCount = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ResizeConstants
    {
        public int SourceWidth;
        public int SourceHeight;
        public int TargetWidth;
        public int TargetHeight;
    }

    public static void UpdateRecord()
    {
        _videoEncoder?.Update();
        if (RecordEnabled && RecordIncludeAudio)
            PumpRecordAudio();

        UpdateRecordAudioEndCheck();
    }

    private static void PumpRecordAudio()
    {
        if (_videoEncoder == null)
            return;

        var now = GetRecordWallClockSeconds();
        if (_lastAudioPumpTime < 0)
            _lastAudioPumpTime = 0;

        var elapsed = now - _lastAudioPumpTime;
        if (elapsed <= 0)
            return;

        var chunkStart = _lastAudioPumpTime;
        _lastAudioPumpTime = now;

        if (!_recordingUsesLoadedAudio)
        {
            var hasStream = AudioEngine.TryGetMainSoundtrackStream(out var streamHandle, out _, out _);
            if (hasStream)
                LiveAudioCapture.EnsureAttached(streamHandle);
        }

        var samplesToRead = Math.Max(1, (int)Math.Round(elapsed * LiveAudioCapture.SampleRate));
        var (audioFrame, _) = LiveAudioCapture.ReadSamples(samplesToRead);
        _videoEncoder.PumpAudio(audioFrame, chunkStart, elapsed);
    }

    private static void UpdateRecordAudioEndCheck()
    {
        if (!RecordEnabled || !_recordingUsesLoadedAudio)
            return;

        RecordAudioPlayback.MaintainPlaybackDuringRecording();

        var wallClock = GetRecordWallClockSeconds();
        var duration = RecordAudioPlayback.DurationSeconds;
        if (duration <= 0)
            return;

        if (wallClock < duration - 0.25)
            return;

        if (RecordAudioPlayback.ShouldStopRecording())
            StopRecording();
    }

    public static bool IsCaptureFrameDue()
    {
        if (!RecordEnabled)
            return false;

        var frameInterval = 1.0 / Math.Max(RecordFps, 1);
        return _lastCaptureRunTime < 0 || GetRecordWallClockSeconds() - _lastCaptureRunTime >= frameInterval;
    }

    public static void CaptureFrameIfDue(Texture2D? source)
    {
        if (!RecordEnabled || source == null || source.IsDisposed || _videoEncoder == null)
            return;

        var frameInterval = 1.0 / Math.Max(RecordFps, 1);
        var now = GetRecordWallClockSeconds();
        if (_lastCaptureRunTime >= 0 && now - _lastCaptureRunTime < frameInterval)
            return;

        var elapsedSinceLast = _lastCaptureRunTime < 0 ? frameInterval : now - _lastCaptureRunTime;
        var captureTimeSeconds = _lastCaptureRunTime < 0 ? 0 : _lastCaptureRunTime;
        _lastCaptureRunTime = now;

        var sourceDesc = source.Description;
        var needsResize = RecordTargetWidth != sourceDesc.Width || RecordTargetHeight != sourceDesc.Height;
        Texture2D textureToEncode;
        if (needsResize)
            textureToEncode = ResizeTexture(source, RecordTargetWidth, RecordTargetHeight) ?? source;
        else
            textureToEncode = source;

        var snapshotTexture = CopyToRecordSnapshot(textureToEncode) ?? textureToEncode;

        try
        {
            if (!_videoEncoder.ProcessVideoFrame(snapshotTexture, captureTimeSeconds, elapsedSinceLast))
            {
                if (!string.IsNullOrEmpty(_videoEncoder.LastError))
                    RecordStatusMessage = _videoEncoder.LastError;
            }
            else
            {
                RecordStatusMessage = $"Aufnahme: {_videoEncoder.FramesWritten} Frames -> {Path.GetFileName(_videoEncoder.FilePath)}";
            }
        }
        catch (Exception ex)
        {
            RecordStatusMessage = $"Aufnahme-Fehler: {ex.Message}";
            RecordEnabled = false;
        }
    }

    public static bool StartRecording()
    {
        RecordStatusMessage = null;
        if (string.IsNullOrWhiteSpace(RecordOutputFolder))
        {
            RecordStatusMessage = "Speicherort waehlen.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(RecordOutputFolder);
        }
        catch (Exception e)
        {
            RecordStatusMessage = $"Ordner nicht erstellbar: {e.Message}";
            return false;
        }

        if (RecordIncludeAudio
            && !string.IsNullOrWhiteSpace(RecordAudioFilePath)
            && !File.Exists(RecordAudioFilePath))
        {
            RecordStatusMessage = $"Audio-Datei nicht gefunden: {RecordAudioFilePath}";
            return false;
        }

        var outputFile = Path.Combine(RecordOutputFolder, $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        var channels = 2;
        var sampleRate = 48000;
        var streamHandle = 0;
        var useLoadedAudioFile = RecordIncludeAudio
                                 && !string.IsNullOrWhiteSpace(RecordAudioFilePath)
                                 && File.Exists(RecordAudioFilePath);
        var hasBassStream = false;
        var useWasapiCapture = false;

        if (useLoadedAudioFile)
        {
            if (!RecordAudioPlayback.TryPrepareStream(out streamHandle, out channels, out sampleRate))
            {
                RecordStatusMessage = "Audio-Datei konnte nicht geladen werden.";
                return false;
            }
        }
        else
        {
            hasBassStream = AudioEngine.TryGetMainSoundtrackStream(out streamHandle, out channels, out sampleRate);

            if (!hasBassStream && RecordIncludeAudio
                && !string.IsNullOrEmpty(WasapiAudioInput.ActiveInputDeviceName))
            {
                useWasapiCapture = true;
                channels = 2;
                sampleRate = WasapiAudioInput.ActiveMixSampleRate;
            }
        }

        try
        {
            _videoEncoder?.Dispose();
            _videoEncoder = new RecordVideoEncoder(
                outputFile,
                RecordTargetWidth,
                RecordTargetHeight,
                RecordCodec,
                RecordBitrateBps,
                RecordFps,
                RecordIncludeAudio,
                channels,
                sampleRate);

            if (RecordIncludeAudio)
            {
                LiveAudioCapture.Start();
                if (useLoadedAudioFile)
                {
                    LiveAudioCapture.DisableWasapiCapture();
                    LiveAudioCapture.EnsureAttached(streamHandle, clearBuffer: false);
                    RecordAudioPlayback.StartPlayback();
                    _playback.TimeInSecs = 0;
                    _playback.PlaybackSpeed = 1.0;
                    _recordingUsesLoadedAudio = true;
                }
                else if (hasBassStream)
                {
                    LiveAudioCapture.EnsureAttached(streamHandle);
                }
                else if (useWasapiCapture)
                {
                    LiveAudioCapture.ConfigureWasapiCapture(channels, sampleRate);
                }
            }

            _lastCaptureRunTime = -1.0;
            _lastAudioPumpTime = 0.0;
            _recordStartRunTime = Playback.RunTimeInSecs;
            EnsureRecordSnapshotTextures(RecordTargetWidth, RecordTargetHeight, SharpDX.DXGI.Format.B8G8R8A8_UNorm);
            RecordEnabled = true;
            RecordStatusMessage = $"Aufnahme gestartet: {Path.GetFileName(outputFile)} ({RecordCodec}, {RecordBitrateBps / 1_000_000} Mbit/s, {RecordFps} FPS)";
            Log.Debug(RecordStatusMessage);
            return true;
        }
        catch (Exception ex)
        {
            RecordStatusMessage = $"Encoder-Start fehlgeschlagen ({RecordCodec}): {ex.Message}";
            Log.Warning(RecordStatusMessage);
            _videoEncoder?.Dispose();
            _videoEncoder = null;
            LiveAudioCapture.Stop();
            if (useLoadedAudioFile || _recordingUsesLoadedAudio)
            {
                RecordAudioPlayback.Stop();
                _recordingUsesLoadedAudio = false;
            }
            RecordEnabled = false;
            return false;
        }
    }

    public static void StopRecording()
    {
        RecordEnabled = false;
        _lastCaptureRunTime = -1.0;
        _lastAudioPumpTime = -1.0;
        _recordStartRunTime = -1.0;
        LiveAudioCapture.Stop();

        if (_recordingUsesLoadedAudio)
        {
            RecordAudioPlayback.Stop();
            _recordingUsesLoadedAudio = false;
        }

        if (_videoEncoder != null)
        {
            var path = _videoEncoder.FilePath;
            var frames = _videoEncoder.FramesWritten;
            try
            {
                _videoEncoder.Dispose();
                RecordStatusMessage = frames > 0
                    ? $"Aufnahme gespeichert: {path} ({frames} Frames)"
                    : $"Aufnahme beendet (keine Frames geschrieben): {path}";
            }
            catch (Exception ex)
            {
                RecordStatusMessage = $"Aufnahme beendet mit Fehler: {ex.Message}";
            }

            _videoEncoder = null;
        }

        DisposeRecordSnapshotTextures();
    }

    private static double GetRecordWallClockSeconds()
    {
        return _recordStartRunTime < 0
            ? 0
            : Math.Max(0, Playback.RunTimeInSecs - _recordStartRunTime);
    }

    private static Texture2D? CopyToRecordSnapshot(Texture2D source)
    {
        if (_deviceContext == null || source == null || source.IsDisposed)
            return null;

        var desc = source.Description;
        EnsureRecordSnapshotTextures(desc.Width, desc.Height, desc.Format);
        if (_recordSnapshotTextures == null || _recordSnapshotTextures.Length == 0)
            return null;

        var snapshot = _recordSnapshotTextures[_recordSnapshotIndex % RecordSnapshotCount];
        _recordSnapshotIndex++;
        _deviceContext.CopyResource(source, snapshot);
        return snapshot;
    }

    private static void EnsureRecordSnapshotTextures(int width, int height, SharpDX.DXGI.Format format)
    {
        if (_recordSnapshotTextures != null
            && _recordSnapshotTextures.Length > 0
            && !_recordSnapshotTextures[0].IsDisposed
            && _recordSnapshotTextures[0].Description.Width == width
            && _recordSnapshotTextures[0].Description.Height == height
            && _recordSnapshotTextures[0].Description.Format == format)
        {
            return;
        }

        DisposeRecordSnapshotTextures();

        _recordSnapshotTextures = new Texture2D[RecordSnapshotCount];
        for (var i = 0; i < RecordSnapshotCount; i++)
        {
            var snapshotDesc = new Texture2DDescription
            {
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget | BindFlags.UnorderedAccess,
                Format = format,
                Width = width,
                Height = height,
                MipLevels = 1,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                OptionFlags = ResourceOptionFlags.None,
                CpuAccessFlags = CpuAccessFlags.None,
                ArraySize = 1,
            };
            _recordSnapshotTextures[i] = Texture2D.CreateTexture2D(snapshotDesc);
        }

        _recordSnapshotIndex = 0;
    }

    private static void DisposeRecordSnapshotTextures()
    {
        if (_recordSnapshotTextures == null)
            return;

        foreach (var texture in _recordSnapshotTextures)
            texture?.Dispose();

        _recordSnapshotTextures = null;
        _recordSnapshotIndex = 0;
    }

    private static Texture2D? ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
    {
        if (_device == null || _deviceContext == null)
            return null;

        var sourceDesc = source.Description;
        if (sourceDesc.Width == targetWidth && sourceDesc.Height == targetHeight)
            return source;

        try
        {
            PrepareResizeResources(sourceDesc.Width, sourceDesc.Height, targetWidth, targetHeight);
            if (_resizeComputeShaderResource?.Value == null || _resizeTargetTexture == null || _resizeTargetUav == null)
                return null;

            var constants = new ResizeConstants
            {
                SourceWidth = sourceDesc.Width,
                SourceHeight = sourceDesc.Height,
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
            };

            _deviceContext.UpdateSubresource(ref constants, _resizeConstantBuffer);
            var csStage = _deviceContext.ComputeShader;
            var prevShader = csStage.Get();
            var prevUavs = csStage.GetUnorderedAccessViews(0, 1);
            var prevSrvs = csStage.GetShaderResources(0, 1);
            var prevCb = csStage.GetConstantBuffers(0, 1);

            csStage.Set(_resizeComputeShaderResource.Value);
            csStage.SetConstantBuffer(0, _resizeConstantBuffer);
            csStage.SetShaderResource(0, SrvManager.GetSrvForTexture(source));
            csStage.SetUnorderedAccessView(0, _resizeTargetUav, 0);

            var dispatchX = (targetWidth + ResizeThreadGroupSize - 1) / ResizeThreadGroupSize;
            var dispatchY = (targetHeight + ResizeThreadGroupSize - 1) / ResizeThreadGroupSize;
            _deviceContext.Dispatch(dispatchX, dispatchY, 1);

            csStage.SetUnorderedAccessView(0, prevUavs[0]);
            csStage.SetShaderResource(0, prevSrvs[0]);
            csStage.SetConstantBuffer(0, prevCb[0]);
            csStage.Set(prevShader);

            return _resizeTargetTexture;
        }
        catch (Exception e)
        {
            Log.Warning($"Record resize failed: {e.Message}");
            return null;
        }
    }

    private static void PrepareResizeResources(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (_resizeTargetTexture != null
            && _resizeTargetTexture.Description.Width == targetWidth
            && _resizeTargetTexture.Description.Height == targetHeight)
        {
            if (_resizeComputeShaderResource?.Value != null)
                return;
        }

        DisposeResizeResources();

        const string sourcePath = @"img\RecordResize-cs.hlsl";
        const string entryPoint = "main";
        _resizeComputeShaderResource = ResourceManager.CreateShaderResource<ComputeShader>(sourcePath, null, () => entryPoint);

        var targetDesc = new Texture2DDescription
        {
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            Width = targetWidth,
            Height = targetHeight,
            MipLevels = 1,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            OptionFlags = ResourceOptionFlags.None,
            CpuAccessFlags = CpuAccessFlags.None,
            ArraySize = 1,
        };
        _resizeTargetTexture = Texture2D.CreateTexture2D(targetDesc);
        _resizeTargetTexture.CreateUnorderedAccessView(ref _resizeTargetUav, "recordResizeUav");

        _resizeConstantBuffer = new SharpDX.Direct3D11.Buffer(
            _device,
            Utilities.SizeOf<ResizeConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
    }

    private static void DisposeResizeResources()
    {
        _resizeTargetUav?.Dispose();
        _resizeTargetUav = null;
        _resizeTargetTexture?.Dispose();
        _resizeTargetTexture = null;
        _resizeConstantBuffer?.Dispose();
        _resizeConstantBuffer = null;
        _resizeComputeShaderResource?.Dispose();
        _resizeComputeShaderResource = null;
    }
}
