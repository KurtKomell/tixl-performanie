using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using DirectShowLib;
using OpenCvSharp;
using SharpDX;
using Newtonsoft.Json;
using Device = SharpDX.Direct3D11.Device;
using FormatType = DirectShowLib.FormatType;
using Lib.io.file;

namespace Lib.io.video;

[Guid("cd5a182e-254b-4e65-820b-ff754122614c")]
public class VideoDeviceInput : Instance<VideoDeviceInput>, ICustomDropdownHolder
{
    private const float Epsilon = 1e-4f;

    // Static device information
    private static List<WebcamWithIndex> _webcamWithIndices;
    private static Dictionary<string, List<(int Width, int Height, double Fps)>> _webcamCapabilities;
    private static readonly object _capabilitiesLock = new();
    private static readonly Lock _staticInitLock = new();
    private static bool _devicesScanned;
    private readonly Lock _captureDeviceLock = new();
    private readonly Lock _lockObject = new();

    [Input(Guid = "236D4C5C-0022-4416-A22C-D6DF73C306E2")]
    public readonly InputSlot<bool> Active = new();

    [Input(Guid = "3022DE8A-5D88-4A37-9799-780F2A838A6E")]
    public readonly InputSlot<float> ApplyRotationData = new();

    [Input(Guid = "11F7432D-31F1-44B9-8F75-B1569B314B13")]
    public readonly InputSlot<int> CustomFps = new();

    [Input(Guid = "C9E1C1F6-3A18-4A1C-8A5E-4B4119965B6E")]
    public readonly InputSlot<Int2> CustomResolution = new();

    [Input(Guid = "22513B82-E77A-417A-8A46-24E677F072D4")]
    public readonly InputSlot<bool> DeactivateWhenNotShowing = new();

    [Input(Guid = "A57E815D-70C9-4D3B-998C-D13506B8F56E")]
    public readonly InputSlot<bool> FlipHorizontally = new();

    [Input(Guid = "3022DE8A-5D88-4A37-9799-780F2A838A5F")]
    public readonly InputSlot<bool> FlipVertically = new();

    [Input(Guid = "f5b900ec-ee17-123e-9972-cdd0580c104e")]
    public readonly InputSlot<string> InputDeviceName = new();

    [Input(Guid = "8D2C28C7-1234-40E2-9388-75574519543D")]
    public readonly InputSlot<bool> OpenSettings = new();

    [Input(Guid = "67E149A5-F7B6-47BC-B147-3B9B11C19C29")]
    public readonly InputSlot<bool> Reconnect = new();

    [Input(Guid = "F187A997-7E4A-48C6-81F9-2A27F150A68A")]
    public readonly InputSlot<Vector2> Reposition = new();

    [Output(Guid = "9C2E4C11-09B6-4F4D-8F99-4A7372D5F2B5", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Int2> Resolution = new();

    [Input(Guid = "49019D29-873E-4B7C-A897-C575A384A650", MappedType = typeof(ResolutionFpsTypeEnum))]
    public readonly InputSlot<int> ResolutionFpsType = new();

    [Input(Guid = "805602D5-52B2-4A73-A337-12E00C3C91F2")]
    public readonly InputSlot<Vector2> Scale = new();

    [Output(Guid = "A1B2C3D4-5678-90EF-1234-567890ABCDEF", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public new readonly Slot<string> Status = new();

    // Output slots
    [Output(Guid = "1d0159cc-33d2-46b1-9c0c-7054aa560df5", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> Texture = new();

    [Output(Guid = "868D5FFE-032C-4522-B56B-D96B30841DB7", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    private CancellationTokenSource _cancellationTokenSource;

    // Instance variables
    private VideoCapture _capture;
    private Thread _captureThread;
    private Texture2D _gpuTexture;
    private int _height;
    private volatile string _lastStatusMessage = "";
    private Mat _sharedMat;
    private int _storeDeviceIndex = -1;
    private string _storeDeviceName = "";
    private string _lastRequestedDeviceName = "";
    // Transformation cache
    private Mat _transformationMatrix;
    private int _width;
    private string grundpath;
    private Settings settings;

    public VideoDeviceInput()
    {
        grundpath = (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Performanie"));
        var json = File.ReadAllText(Path.Combine(grundpath, "settings.pers"));
        settings  = JsonConvert.DeserializeObject<Settings>(json);
        Texture.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
        Resolution.UpdateAction = Update;
        Status.UpdateAction = Update;
    }

    private void SetStatus(string message)
    {
        _lastStatusMessage = message;
    }

    private void Update(EvaluationContext context)
    {
        ScanWebCamDevices();

        var deviceName = InputDeviceName.GetValue(context);

        // Do not auto-grab the first camera when no name is set. Two CameraInput
        // instances would otherwise both open the same USB device → exclusive lock / AV.
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            if (IsCaptureThreadRunning())
                StopCaptureThread();
            SetStatus("Waiting for camera device name…");
            Status.Value = _lastStatusMessage;
            return;
        }

        var deviceNameChanged = !string.Equals(deviceName, _lastRequestedDeviceName, StringComparison.Ordinal);
        if (deviceNameChanged)
            _lastRequestedDeviceName = deviceName;

        if (!TryGetIndexForDeviceName(deviceName, out var selectedDeviceIndex))
        {
            // Device list can be stale (USB plugged after first scan).
            ForceRescanWebCamDevices();
            if (!TryGetIndexForDeviceName(deviceName, out selectedDeviceIndex))
            {
                SetStatus($"Error: Camera '{deviceName}' not found.");
                StopCaptureThread();
                Status.Value = _lastStatusMessage;
                return;
            }
        }

        var deviceIsActive = Active.GetValue(context);
        var deactivateWhenNotShowing = DeactivateWhenNotShowing.GetValue(context);

        bool shouldBeCapturing = deviceIsActive && (!deactivateWhenNotShowing || IsActiveInGraph());

        bool reconnectTriggered = Reconnect.GetValue(context);
        if (reconnectTriggered)
        {
            Reconnect.SetTypedInputValue(false);
        }

        if (shouldBeCapturing)
        {
            if (IsCaptureThreadRunning() &&
                (selectedDeviceIndex != _storeDeviceIndex || reconnectTriggered || deviceNameChanged))
            {
                Log.Debug($"VideoDeviceInput: switching camera → '{deviceName}' (index {selectedDeviceIndex}, reconnect={reconnectTriggered}, nameChanged={deviceNameChanged})");
                StopCaptureThread();
            }

            if (!IsCaptureThreadRunning())
            {
                _storeDeviceIndex = selectedDeviceIndex;
                _storeDeviceName = deviceName;
                StartCaptureThread(context);
            }
        }
        else
        {
            StopCaptureThread();
        }

        if (OpenSettings.GetValue(context))
        {
            OpenVideoSettings();
            OpenSettings.SetTypedInputValue(false);
        }

        lock (_lockObject)
        {
            if (_sharedMat != null && !_sharedMat.Empty())
            {
                UploadMat(ResourceManager.Device, _sharedMat);
                Texture.Value = _gpuTexture;
                UpdateCount.Value++;
                Resolution.Value = new Int2(_sharedMat.Width, _sharedMat.Height);
            }
        }

        Status.Value = _lastStatusMessage;
    }

    private bool IsCaptureThreadRunning()
    {
        return _captureThread != null && _captureThread.IsAlive;
    }

    private bool IsActiveInGraph() => true;

    private void StartCaptureThread(EvaluationContext context)
    {
        if (IsCaptureThreadRunning()) return;

        var settings = new CaptureThreadSettings(this, context);
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        _captureThread = new Thread(() => CaptureLoop(settings, token))
                             {
                                 IsBackground = true,
                                 Name = "Video Capture Thread",
                                 Priority = ThreadPriority.BelowNormal
                             };
        _captureThread.Start();
    }

    private void StopCaptureThread()
    {
        if (!IsCaptureThreadRunning() && _capture == null)
            return;

        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch
        {
            /* ignore */
        }

        var thread = _captureThread;
        if (thread != null && thread.IsAlive)
        {
            // Wait for CaptureLoop.finally to Release/Dispose the device.
            if (!thread.Join(TimeSpan.FromSeconds(4)))
            {
                Log.Warning("Video capture thread did not exit in time — camera may stay locked until process exit.");
                // Do NOT Dispose _capture here while the thread may still use it (AV).
                _captureThread = null;
                return;
            }
        }

        _captureThread = null;

        try
        {
            _cancellationTokenSource?.Dispose();
        }
        catch
        {
            /* ignore */
        }

        _cancellationTokenSource = null;
        SafeReleaseCapture();
    }

    private void SafeReleaseCapture()
    {
        lock (_captureDeviceLock)
        {
            if (_capture == null)
                return;

            try
            {
                if (_capture.IsOpened())
                    _capture.Release();
            }
            catch (Exception e)
            {
                Log.Debug($"VideoCapture.Release failed: {e.Message}");
            }

            try
            {
                _capture.Dispose();
            }
            catch (Exception e)
            {
                Log.Debug($"VideoCapture.Dispose failed: {e.Message}");
            }

            _capture = null;
        }
    }

    private void CaptureLoop(CaptureThreadSettings settings, CancellationToken token)
    {
        try
        {
            // Resolve the selected camera by name at open time. Never fall back to index 0.
            var deviceIndex = settings.DeviceIndex;
            var deviceName = settings.DeviceName?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(deviceName) &&
                TryGetIndexForDeviceName(deviceName, out var resolvedIndex))
            {
                deviceIndex = resolvedIndex;
            }

            if (deviceIndex < 0 && string.IsNullOrWhiteSpace(deviceName))
            {
                SetStatus("Error: No camera selected.");
                return;
            }

            _storeDeviceIndex = deviceIndex;

            lock (_captureDeviceLock)
            {
                if (token.IsCancellationRequested) return;

                // Prefer opening by FriendlyName — DirectShowLib indices can diverge from
                // OpenCV's DSHOW indices when NDI/OBS virtual cams are present.
                // Never Set() resolution before Open: that makes DSHOW grab camera 0.
                if (!TryOpenCapture(deviceName, deviceIndex, out _capture))
                {
                    SetStatus($"Error: Failed to open video capture device '{deviceName}' (index {deviceIndex}).");
                    return;
                }

                Log.Debug($"VideoDeviceInput: opened '{deviceName}' (index {deviceIndex})");
            }

            Thread.Sleep(100);

            lock (_captureDeviceLock)
            {
                if (token.IsCancellationRequested || _capture == null) return;

                if (settings.ResolutionFpsType == ResolutionFpsTypeEnum.Custom)
                {
                    bool widthSet = _capture.Set(VideoCaptureProperties.FrameWidth, settings.CustomResolution.X);
                    bool heightSet = _capture.Set(VideoCaptureProperties.FrameHeight, settings.CustomResolution.Y);
                    bool fpsSet = _capture.Set(VideoCaptureProperties.Fps, settings.CustomFps);

                    if (!widthSet || !heightSet || !fpsSet)
                    {
                        Log.Debug("Set returned false—camera doesn't support requested settings.");
                    }

                    var actualWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                    var actualHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                    var actualFps = _capture.Get(VideoCaptureProperties.Fps);

                    if (actualWidth != settings.CustomResolution.X || actualHeight != settings.CustomResolution.Y ||
                        Math.Abs(actualFps - settings.CustomFps) > 0.01)
                    {
                        SetStatus($"Warning: Using {actualWidth}x{actualHeight}@{actualFps:F2}fps (requested {settings.CustomResolution.X}x{settings.CustomResolution.Y}@{settings.CustomFps}fps).");
                    }
                    else
                    {
                        SetStatus($"Success: Running at {settings.CustomResolution.X}x{settings.CustomResolution.Y}@{settings.CustomFps}fps.");
                    }
                }
                else
                {
                    SetStatus("Running at device default settings.");
                }
            }

            using var frame = new Mat();
            using var bgraMat = new Mat();

            while (!token.IsCancellationRequested)
            {
                Mat currentFrame = null;
                bool frameReadSuccess = false;

                lock (_captureDeviceLock)
                {
                    if (_capture != null && _capture.IsOpened())
                    {
                        frameReadSuccess = _capture.Read(frame);
                    }
                }

                if (frameReadSuccess && !frame.Empty())
                {
                    SetStatus("Running");
                    Cv2.CvtColor(frame, bgraMat, ColorConversionCodes.BGR2BGRA);

                    if (settings.FlipVertically)
                        Cv2.Flip(bgraMat, bgraMat, FlipMode.Y);

                    if (settings.FlipHorizontally)
                        Cv2.Flip(bgraMat, bgraMat, FlipMode.X);

                    if (settings.ResolutionFpsType == ResolutionFpsTypeEnum.Custom)
                    {
                        var targetSize = new Size(settings.CustomResolution.X, settings.CustomResolution.Y);
                        if (bgraMat.Size() != targetSize)
                        {
                            using var resized = new Mat();
                            Cv2.Resize(bgraMat, resized, targetSize, 0, 0, InterpolationFlags.Cubic);
                            currentFrame = resized.Clone();
                            SetStatus(_lastStatusMessage + " (Software resize)");
                        }
                    }

                    currentFrame ??= bgraMat;

                    if (settings.HasTransformation())
                    {
                        UpdateTransformationMatrix(settings, currentFrame.Size());
                        var newSize = new Size(currentFrame.Width, currentFrame.Height);

                        lock (_lockObject)
                        {
                            if (_sharedMat == null || _sharedMat.Width != newSize.Width || _sharedMat.Height != newSize.Height)
                            {
                                _sharedMat?.Dispose();
                                _sharedMat = new Mat(newSize, MatType.CV_8UC4);
                            }

                            Cv2.WarpAffine(currentFrame, _sharedMat, _transformationMatrix, newSize);
                        }
                    }
                    else
                    {
                        lock (_lockObject)
                        {
                            if (_sharedMat == null || _sharedMat.Width != currentFrame.Width || _sharedMat.Height != currentFrame.Height)
                            {
                                _sharedMat?.Dispose();
                                _sharedMat = new Mat(currentFrame.Height, currentFrame.Width, MatType.CV_8UC4);
                            }

                            currentFrame.CopyTo(_sharedMat);
                        }
                    }

                    if (currentFrame != bgraMat)
                    {
                        currentFrame.Dispose();
                    }
                }
                else
                {
                    SetStatus("Error: Failed to retrieve frame.");
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception e)
        {
            Log.Debug($"Video capture thread failed: {e.Message}\n{e.StackTrace}");
            SetStatus($"Error: Capture failed - {e.Message}");
        }
        finally
        {
            SafeReleaseCapture();
            try
            {
                _transformationMatrix?.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private void UpdateTransformationMatrix(CaptureThreadSettings settings, Size frameSize)
    {
        _transformationMatrix?.Dispose();
        _transformationMatrix = new Mat(2, 3, MatType.CV_32FC1);

        var center = new Point2f(frameSize.Width / 2.0f, frameSize.Height / 2.0f);
        var angleRad = settings.Rotation * Math.PI / 180.0;
        var cos = (float)Math.Cos(angleRad);
        var sin = (float)Math.Sin(angleRad);

        _transformationMatrix.Set(0, 0, cos * settings.Scale.X);
        _transformationMatrix.Set(0, 1, sin * settings.Scale.Y);
        _transformationMatrix.Set(1, 0, -sin * settings.Scale.X);
        _transformationMatrix.Set(1, 1, cos * settings.Scale.Y);

        var tx = (1.0f - cos * settings.Scale.X) * center.X - sin * settings.Scale.Y * center.Y;
        var ty = sin * settings.Scale.X * center.X + (1.0f - cos * settings.Scale.Y) * center.Y;

        _transformationMatrix.Set(0, 2, settings.Reposition.X + tx);
        _transformationMatrix.Set(1, 2, settings.Reposition.Y + ty);
    }

    private void UploadMat(Device device, Mat mat)
    {
        if (mat == null || mat.Empty())
        {
            Log.Debug("UploadMat: Mat is null or empty. Skipping upload.");
            return;
        }

        var width = mat.Width;
        var height = mat.Height;

        if (_gpuTexture == null || _width != width || _height != height)
        {
            _gpuTexture?.Dispose();
            _width = width;
            _height = height;

            var texDesc = new Texture2DDescription
                              {
                                  Width = width,
                                  Height = height,
                                  MipLevels = 1,
                                  ArraySize = 1,
                                  Format = Format.B8G8R8A8_UNorm,
                                  SampleDescription = new SampleDescription(1, 0),
                                  Usage = ResourceUsage.Default,
                                  BindFlags = BindFlags.ShaderResource,
                                  CpuAccessFlags = CpuAccessFlags.None,
                                  OptionFlags = ResourceOptionFlags.None
                              };
            var sharpDxTexture = new SharpDX.Direct3D11.Texture2D(device, texDesc);
            _gpuTexture = new Texture2D(sharpDxTexture);
        }

        var dataBox = new DataBox(mat.Data, (int)mat.Step(), 0);
        device.ImmediateContext.UpdateSubresource(dataBox, _gpuTexture);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        StopCaptureThread();

        _gpuTexture?.Dispose();
        _gpuTexture = null;

        _transformationMatrix?.Dispose();
        _transformationMatrix = null;

        lock (_lockObject)
        {
            _sharedMat?.Dispose();
            _sharedMat = null;
        }
    }

    private sealed record CaptureThreadSettings
    {
        public readonly int CustomFps;
        public readonly Int2 CustomResolution;
        public readonly int DeviceIndex;
        public readonly string DeviceName;
        public readonly bool FlipHorizontally;
        public readonly bool FlipVertically;
        public readonly Vector2 Reposition;
        public readonly ResolutionFpsTypeEnum ResolutionFpsType;
        public readonly float Rotation;
        public readonly Vector2 Scale;

        public CaptureThreadSettings(VideoDeviceInput owner, EvaluationContext context)
        {
            ResolutionFpsType = (ResolutionFpsTypeEnum)owner.ResolutionFpsType.GetValue(context);
            CustomResolution = owner.CustomResolution.GetValue(context);
            CustomFps = owner.CustomFps.GetValue(context);
            FlipVertically = owner.FlipVertically.GetValue(context);
            FlipHorizontally = owner.FlipHorizontally.GetValue(context);
            Rotation = owner.ApplyRotationData.GetValue(context);
            Reposition = owner.Reposition.GetValue(context);
            Scale = owner.Scale.GetValue(context);
            DeviceName = owner._storeDeviceName ?? owner.InputDeviceName.GetValue(context) ?? string.Empty;
            DeviceIndex = owner._storeDeviceIndex;
        }

        public bool HasTransformation()
        {
            return Math.Abs(Rotation) > Epsilon ||
                   Math.Abs(Scale.X - 1.0f) > Epsilon || Math.Abs(Scale.Y - 1.0f) > Epsilon ||
                   Math.Abs(Reposition.X) > Epsilon || Math.Abs(Reposition.Y) > Epsilon;
        }
    }

    private enum ResolutionFpsTypeEnum
    {
        DeviceDefault = 0,
        Custom = 1
    }

    #region Device Configuration UI
    private void OpenVideoSettings()
    {
        try
        {
            lock (_captureDeviceLock)
            {
                if (_capture != null && _capture.IsOpened())
                {
                    _capture.Set(VideoCaptureProperties.Settings, 1);
                }
                else
                {
                    Log.Debug("Cannot open settings, capture device is not available.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to open video settings: {ex.Message}");
        }
    }

    public void OpenCrossbarSettings()
    {
        Log.Debug("Crossbar settings not implemented for this version.");
    }
    #endregion

    #region Dropdown UI
    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == InputDeviceName.Id) return InputDeviceName.Value;
        if (inputId == CustomResolution.Id) return $"{CustomResolution.Value.X}x{CustomResolution.Value.Y}";
        if (inputId == CustomFps.Id) return CustomFps.Value.ToString();
        return string.Empty;
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == InputDeviceName.Id)
        {
            if (_webcamWithIndices == null || _webcamWithIndices.Count == 0)
            {
                yield return "No devices found";
                yield break;
            }

            foreach (var (webcam, _) in _webcamWithIndices)
                yield return webcam;
        }
        else if (inputId == CustomResolution.Id)
        {
            var deviceName = InputDeviceName.Value;
            lock (_capabilitiesLock)
            {
                if (_webcamCapabilities != null && _webcamCapabilities.ContainsKey(deviceName))
                {
                    foreach (var (w, h, _) in _webcamCapabilities[deviceName].DistinctBy(c => (c.Width, c.Height)))
                        yield return $"{w}x{h}";
                }
                else
                    yield return "640x480";
            }
        }
        else if (inputId == CustomFps.Id)
        {
            var deviceName = InputDeviceName.Value;
            lock (_capabilitiesLock)
            {
                if (_webcamCapabilities != null && _webcamCapabilities.ContainsKey(deviceName))
                {
                    foreach (var (_, _, fps) in _webcamCapabilities[deviceName].DistinctBy(c => c.Fps))
                        yield return $"{fps:F0}";
                }
                else
                    yield return "30";
            }
        }
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string selected, bool isAListItem)
    {
        if (inputId == InputDeviceName.Id)
            InputDeviceName.SetTypedInputValue(selected);
        else if (inputId == CustomResolution.Id)
        {
            if (selected != null)
            {
                var parts = selected.Split('x');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var w) &&
                    int.TryParse(parts[1], out var h))
                {
                    CustomResolution.SetTypedInputValue(new Int2(w, h));

                    var deviceName = InputDeviceName.Value;
                    lock (_capabilitiesLock)
                    {
                        if (_webcamCapabilities.TryGetValue(deviceName, out var caps) &&
                            !caps.Any(c => c.Width == w && c.Height == h))
                        {
                            SetStatus("Warning: Unsupported resolution—may fallback.");
                        }
                    }
                }
            }
        }
        else if (inputId == CustomFps.Id)
        {
            if (double.TryParse(selected, out var fps))
            {
                CustomFps.SetTypedInputValue((int)fps);
            }
        }
    }
    #endregion

    #region Scanning for webcams and capabilities
    private static void ForceRescanWebCamDevices()
    {
        lock (_staticInitLock)
        {
            _devicesScanned = false;
        }

        ScanWebCamDevices();
    }

    private static bool TryOpenCapture(string deviceName, int deviceIndex, out VideoCapture capture)
    {
        capture = null;

        // 1) By FriendlyName via DSHOW (most reliable when virtual cams shift indices)
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            if (TryOpenCaptureOnce($"video={deviceName}", VideoCaptureAPIs.DSHOW, out capture))
                return true;
        }

        // 2) By index via DSHOW (empty ctor + Open — no property Set before Open)
        if (deviceIndex >= 0)
        {
            if (TryOpenCaptureByIndex(deviceIndex, VideoCaptureAPIs.DSHOW, out capture))
                return true;

            // 3) MSMF fallback by index
            if (TryOpenCaptureByIndex(deviceIndex, VideoCaptureAPIs.MSMF, out capture))
                return true;
        }

        return false;
    }

    private static bool TryOpenCaptureOnce(string source, VideoCaptureAPIs api, out VideoCapture capture)
    {
        capture = null;
        try
        {
            var candidate = new VideoCapture(source, api);
            if (candidate.IsOpened())
            {
                capture = candidate;
                return true;
            }

            candidate.Dispose();
        }
        catch (Exception e)
        {
            Log.Debug($"VideoDeviceInput: open '{source}' via {api} failed: {e.Message}");
        }

        return false;
    }

    private static bool TryOpenCaptureByIndex(int index, VideoCaptureAPIs api, out VideoCapture capture)
    {
        capture = null;
        try
        {
            var candidate = new VideoCapture();
            if (candidate.Open(index, api) && candidate.IsOpened())
            {
                capture = candidate;
                return true;
            }

            candidate.Dispose();
        }
        catch (Exception e)
        {
            Log.Debug($"VideoDeviceInput: open index {index} via {api} failed: {e.Message}");
        }

        return false;
    }

    public static void ScanWebCamDevices()
    {
        lock (_staticInitLock)
        {
            if (_devicesScanned)
                return;

            _webcamWithIndices = new List<WebcamWithIndex>();
            lock (_capabilitiesLock)
            {
                _webcamCapabilities = new Dictionary<string, List<(int Width, int Height, double Fps)>>();
            }

            try
            {
                // Only enumerate FriendlyNames — do NOT BindToObject every device here.
                // Binding all cams at scan time exclusive-locks USB cameras (e.g. HD USB Camera)
                // and prevents a later Open() from applying the selected device.
                var videoInputDevices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                for (var i = 0; i < videoInputDevices.Length; i++)
                {
                    var device = videoInputDevices[i];
                    // Kinect / Xbox NUI are not usable as CameraInput VideoCapture devices.
                    if (IsExcludedCameraDevice(device.Name))
                    {
                        Log.Debug($"VideoDeviceInput: skipping camera #{i}: {device.Name}");
                        continue;
                    }

                    _webcamWithIndices.Add(new WebcamWithIndex(device.Name, i));
                    lock (_capabilitiesLock)
                    {
                        _webcamCapabilities[device.Name] = new List<(int Width, int Height, double Fps)>();
                    }

                    Log.Debug($"VideoDeviceInput: found camera #{i}: {device.Name}");
                }
            }
            catch (Exception e)
            {
                Log.Debug("Failed to scan webcams: " + e.Message);
            }

            _devicesScanned = true;
        }
    }

    private static bool IsExcludedCameraDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        return name.Contains("NVIDIA Broadcast", StringComparison.OrdinalIgnoreCase)
               || name.Contains("OBS Virtual Camera", StringComparison.OrdinalIgnoreCase)
               || name.Contains("VCam", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Kinect", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Xbox NUI", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetIndexForDeviceName(string deviceName, out int index)
    {
        index = -1;
        if (_webcamWithIndices == null || string.IsNullOrWhiteSpace(deviceName))
            return false;

        if (IsExcludedCameraDevice(deviceName))
            return false;

        // Exact match first
        var device = _webcamWithIndices.FirstOrDefault(d =>
            string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        if (device != null)
        {
            index = device.Index;
            return true;
        }

        // Tolerant match: Windows sometimes appends " #2" / trims differently across APIs
        var trimmed = deviceName.Trim();
        device = _webcamWithIndices.FirstOrDefault(d =>
            d.Name.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(d.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (device != null)
        {
            index = device.Index;
            Log.Debug($"VideoDeviceInput: fuzzy-matched '{deviceName}' → '{device.Name}' (index {index})");
            return true;
        }

        return false;
    }

    public sealed record WebcamWithIndex(string Name, int Index);
    #endregion

    public class ResolutionCamera
    {
        public int Width { get; set; }

        public int Height { get; set; }

        public double FPS { get; set; }

        public ResolutionCamera(int width, int height, double fps)
        {
            Width = width;
            Height = height;
            FPS = fps;
        }
    }

    public enum TransitionMode
    {
        Key,
        Automatic,
        Cursor
    }

    public class DropdownDataForms
    {
        public string[] Options { get; set; }

        public int Field { get; set; }
        public int SelectedIndex { get; set; }

        public bool ShowTextInput { get; set; }

        public string TextInput { get; set; }

        public bool SpoutEnabled { get; set; }
        public string SpoutInput { get; set; }

        [JsonConstructor]
        public DropdownDataForms(string[] options, int field, int selectedIndex, bool showTextInput, string textInput, bool spoutEnabled, string spoutInput)
        {
            Options = options;
            Field = field;
            SelectedIndex = selectedIndex;
            ShowTextInput = showTextInput;
            SpoutEnabled = spoutEnabled;
            SpoutInput = spoutInput;
            TextInput = textInput;

        }
    }
    public class Settings
    {
        public bool IsWindowed { get; set; }

        public string AudioDevice { get; set; }
        public int GrafikDevice { get; set; }

        public int ResolutionWidth { get; set; }
        public int ResolutionHeight { get; set; }
        public TransitionMode Transitionmode { get; set; }

        public bool AutoselectedScreenInput { get; set; }

        public bool AutoSelectedAudioInput { get; set; }

        public int AudioInputIndex { get; set; }

        public string IndexNames { get; set; }

        public float Time { get; set; }

        public string IndexRoot { get; set; }

        public string Camera { get; set; }

        public ResolutionCamera CameraResolution { get; set; }

        [JsonConstructor]
        public Settings(bool isWindowed, string audioDevice, int grafikDevice, int resolutionwidth, int resolutionheight, TransitionMode transitionmode, bool autoselectedscreeninput, bool autoselectedaudioinput, int audioinputindex, string indexnames, float time, string indexroot, string camera, ResolutionCamera cameraresolution)
        {
            IsWindowed = isWindowed;
            AudioDevice = audioDevice;
            GrafikDevice = grafikDevice;
            ResolutionWidth = resolutionwidth;
            ResolutionHeight = resolutionheight;
            Transitionmode = transitionmode;
            AutoselectedScreenInput = autoselectedscreeninput;
            AutoSelectedAudioInput = autoselectedaudioinput;
            AudioInputIndex = audioinputindex;
            IndexNames = indexnames;
            Time = time;
            IndexRoot = indexroot;
            Camera = camera;
            CameraResolution = cameraresolution;
        }
    }
}