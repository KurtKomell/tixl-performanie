using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.IO;
using T3.Core.Animation;
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
    public static bool RecordUseNisUpscaler = true;
    public static string RecordOutputFolder = string.Empty;

    private static TextureBgraReadAccess? _recordReadAccess;
    private static double _lastCaptureRunTime = -1.0;
    private static int _recordFrameIndex;
    private static Resource<ComputeShader>? _upscaleComputeShaderResource;
    private static Texture2D? _upscaleTargetTexture;
    private static UnorderedAccessView? _upscaleTargetUav;
    private static SharpDX.Direct3D11.Buffer? _upscaleConstantBuffer;

    private const int RecordCaptureIntervalSecs = 1;
    private const int UpscaleThreadGroupSize = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct UpscaleConstants
    {
        public int SourceWidth;
        public int SourceHeight;
        public int TargetWidth;
        public int TargetHeight;
    }

    public static void UpdateRecord()
    {
        _recordReadAccess?.Update();
    }

    public static void CaptureFrameIfDue(Texture2D? source)
    {
        if (!RecordEnabled || source == null || source.IsDisposed)
            return;

        if (string.IsNullOrWhiteSpace(RecordOutputFolder))
            return;

        var now = Playback.RunTimeInSecs;
        if (_lastCaptureRunTime >= 0 && now - _lastCaptureRunTime < RecordCaptureIntervalSecs)
            return;

        _lastCaptureRunTime = now;
        _recordFrameIndex++;

        try
        {
            Directory.CreateDirectory(RecordOutputFolder);
        }
        catch (Exception e)
        {
            Log.Warning($"Record: failed to create output folder: {e.Message}");
            return;
        }

        var filepath = Path.Combine(RecordOutputFolder, $"frame_{_recordFrameIndex:D6}.png");
        var sourceDesc = source.Description;
        var needsUpscale = RecordTargetWidth != sourceDesc.Width || RecordTargetHeight != sourceDesc.Height;

        Texture2D textureToRead;
        if (needsUpscale && RecordUseNisUpscaler)
        {
            textureToRead = UpscaleTexture(source, RecordTargetWidth, RecordTargetHeight);
            if (textureToRead == null)
            {
                Log.Warning("Record: upscale failed, saving at source resolution.");
                textureToRead = source;
            }
        }
        else if (needsUpscale)
        {
            textureToRead = UpscaleTexture(source, RecordTargetWidth, RecordTargetHeight) ?? source;
        }
        else
        {
            textureToRead = source;
        }

        _recordReadAccess ??= new TextureBgraReadAccess();
        if (!_recordReadAccess.InitiateConvertAndReadBack(textureToRead, OnRecordReadComplete, filepath))
            Log.Warning("Record: failed to initiate readback.");
    }

    private static Texture2D? UpscaleTexture(Texture2D source, int targetWidth, int targetHeight)
    {
        if (_device == null || _deviceContext == null)
            return null;

        var sourceDesc = source.Description;
        if (sourceDesc.Width == targetWidth && sourceDesc.Height == targetHeight)
            return source;

        try
        {
            PrepareUpscaleResources(sourceDesc.Width, sourceDesc.Height, targetWidth, targetHeight);
            if (_upscaleComputeShaderResource?.Value == null || _upscaleTargetTexture == null || _upscaleTargetUav == null)
                return null;

            var constants = new UpscaleConstants
            {
                SourceWidth = sourceDesc.Width,
                SourceHeight = sourceDesc.Height,
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
            };

            _deviceContext.UpdateSubresource(ref constants, _upscaleConstantBuffer);
            var csStage = _deviceContext.ComputeShader;
            var prevShader = csStage.Get();
            var prevUavs = csStage.GetUnorderedAccessViews(0, 1);
            var prevSrvs = csStage.GetShaderResources(0, 1);
            var prevCb = csStage.GetConstantBuffers(0, 1);

            csStage.Set(_upscaleComputeShaderResource.Value);
            csStage.SetConstantBuffer(0, _upscaleConstantBuffer);
            csStage.SetShaderResource(0, SrvManager.GetSrvForTexture(source));
            csStage.SetUnorderedAccessView(0, _upscaleTargetUav, 0);

            var dispatchX = (targetWidth + UpscaleThreadGroupSize - 1) / UpscaleThreadGroupSize;
            var dispatchY = (targetHeight + UpscaleThreadGroupSize - 1) / UpscaleThreadGroupSize;
            _deviceContext.Dispatch(dispatchX, dispatchY, 1);

            csStage.SetUnorderedAccessView(0, prevUavs[0]);
            csStage.SetShaderResource(0, prevSrvs[0]);
            csStage.SetConstantBuffer(0, prevCb[0]);
            csStage.Set(prevShader);

            return _upscaleTargetTexture;
        }
        catch (Exception e)
        {
            Log.Warning($"Record upscale failed: {e.Message}");
            return null;
        }
    }

    private static void PrepareUpscaleResources(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (_upscaleTargetTexture != null
            && _upscaleTargetTexture.Description.Width == targetWidth
            && _upscaleTargetTexture.Description.Height == targetHeight)
        {
            if (_upscaleComputeShaderResource?.Value != null)
                return;
        }

        DisposeUpscaleResources();

        const string sourcePath = @"img\Upscale-cs.hlsl";
        const string entryPoint = "main";
        _upscaleComputeShaderResource = ResourceManager.CreateShaderResource<ComputeShader>(sourcePath, null, () => entryPoint);

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
        _upscaleTargetTexture = Texture2D.CreateTexture2D(targetDesc);
        _upscaleTargetTexture.CreateUnorderedAccessView(ref _upscaleTargetUav, "recordUpscaleUav");

        _upscaleConstantBuffer = new SharpDX.Direct3D11.Buffer(
            _device,
            Utilities.SizeOf<UpscaleConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
    }

    private static void DisposeUpscaleResources()
    {
        _upscaleTargetUav?.Dispose();
        _upscaleTargetUav = null;
        _upscaleTargetTexture?.Dispose();
        _upscaleTargetTexture = null;
        _upscaleConstantBuffer?.Dispose();
        _upscaleConstantBuffer = null;
        _upscaleComputeShaderResource?.Dispose();
        _upscaleComputeShaderResource = null;
    }

    private static void OnRecordReadComplete(TextureBgraReadAccess.ReadRequestItem request)
    {
        if (request.CpuAccessTexture.IsDisposed || string.IsNullOrEmpty(request.Filepath))
            return;

        var context = ResourceManager.Device.ImmediateContext;
        DataStream imageStream;
        try
        {
            var dataBox = context.MapSubresource(
                request.CpuAccessTexture,
                0,
                0,
                MapMode.Read,
                MapFlags.None,
                out imageStream);
            using (imageStream)
            {
                var width = request.CpuAccessTexture.Description.Width;
                var height = request.CpuAccessTexture.Description.Height;
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bitmapData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                var srcPtr = dataBox.DataPointer;
                var dstPtr = bitmapData.Scan0;
                var rowBytes = width * 4;

                for (var y = 0; y < height; y++)
                {
                    Utilities.CopyMemory(dstPtr + y * bitmapData.Stride, srcPtr + y * dataBox.RowPitch, rowBytes);
                }

                bitmap.UnlockBits(bitmapData);
                bitmap.Save(request.Filepath, ImageFormat.Png);
                Log.Debug($"Record saved: {request.Filepath}");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Record: save failed: {e.Message}");
        }
        finally
        {
            try
            {
                context.UnmapSubresource(request.CpuAccessTexture, 0);
            }
            catch
            {
                // ignored
            }
        }
    }

    public static void StopRecording()
    {
        RecordEnabled = false;
        _recordReadAccess?.ClearQueue();
    }
}
