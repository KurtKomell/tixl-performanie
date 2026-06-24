using Operators.Utils;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.Logging;
using T3.Core.Operator.Slots;

namespace T3.Player;

public partial class Program
{
    // todo - share this function with the editor ? is that possible? it could have delegate arguments
    private static void RenderCallback()
    {
        ProcessPendingSwapChainResize();
        if (_renderView == null)
            return;

        _resolution = new Core.DataTypes.Vector.Int2(Program._renderForm.ClientSize.Width, Program._renderForm.ClientSize.Height);

        var viewportW = _backBuffer?.Description.Width ?? _resolution.Width;
        var viewportH = _backBuffer?.Description.Height ?? _resolution.Height;

        WasapiAudioInput.StartFrame(_playback.Settings);
        _playback.Update();
        // Update OSC messages
       
        //Log.Debug($" render at playback time {_playback.TimeInSecs:0.00}s");
        if (_soundtrackHandle != null)
        {
            AudioEngine.UseAudioClip(_soundtrackHandle, _playback.TimeInSecs);
            if (_playback.TimeInSecs >= _soundtrackHandle.Clip.LengthInSeconds + _soundtrackHandle.Clip.StartTime)
            {
                if (_resolvedOptions.Loop)
                {
                    _playback.TimeInSecs = 0.0;
                }
                else
                {
                    throw new TimelineEndedException();
                }
            }
        }

        // Update
        AudioEngine.CompleteFrame(_playback, Playback.LastFrameDuration);

        DirtyFlag.IncrementGlobalTicks();
        DirtyFlag.InvalidationRefFrame++;

        _deviceContext.Rasterizer.SetViewport(new Viewport(0, 0, viewportW, viewportH, 0.0f, 1.0f));
        _deviceContext.OutputMerger.SetTargets(_renderView);

        _evalContext.Reset();
        _evalContext.RequestedResolution = _resolution;
        //Console.WriteLine($"Requested resolution: {_evalContext.RequestedResolution.Width}x{_evalContext.RequestedResolution.Height}");

        if (_textureOutput != null)
        {
            _textureOutput.Invalidate();
            var outputTexture = _textureOutput.GetValue(_evalContext);
            var textureChanged = outputTexture != _outputTexture;

            if (_outputTexture != null || textureChanged)
            {
                _outputTexture = outputTexture;
                _deviceContext.Rasterizer.State = _rasterizerState;
                if (_fullScreenVertexShaderResource?.Value != null)
                    _deviceContext.VertexShader.Set(_fullScreenVertexShaderResource.Value);
                if (_fullScreenPixelShaderResource?.Value != null)
                    _deviceContext.PixelShader.Set(_fullScreenPixelShaderResource.Value);

                if (_outputTextureSrv == null || textureChanged)
                {
                    Log.Debug("Creating new srv...");
                    _outputTextureSrv = new ShaderResourceView(_device, _outputTexture);
                }

                var pixelShader = _deviceContext.PixelShader;
                pixelShader.SetShaderResource(0, _outputTextureSrv);

                _deviceContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                _deviceContext.ClearRenderTargetView(_renderView, new Color(0.45f, 0.55f, 0.6f, 1.0f));
                _deviceContext.Draw(3, 0);
                pixelShader.SetShaderResource(0, null);
            }
        }
        UpdateRecord();
        if (RecordEnabled && _outputTexture != null)
            CaptureFrameIfDue(_outputTexture);

        T3.Player.Program.renderStarted = true;
        _swapChain.Present(_vsyncInterval, PresentFlags.None);
    }
    
    private class TimelineEndedException : Exception
    {
    }
}