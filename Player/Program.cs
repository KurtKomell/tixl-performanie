// NOTE: Enabling this will require Windows Graphics Tools feature to be enabled
// This will prevent the player from running on most Windows systems.
//#define FORCE_D3D_DEBUG
using CommandLine;
using CommandLine.Text;
using ManagedBass;
using Newtonsoft.Json;
using Operators.Utils;
using Rug.Osc;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Windows;
using SilkWindows;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.Compilation;
using T3.Core.DataTypes.Vector;
using T3.Core.IO;
using T3.Core.Logging;
using T3.Core.Model;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Rendering;
using T3.Core.Resource;
using T3.Core.SystemUi;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Serialization;
using Device = SharpDX.Direct3D11.Device;
using DeviceContext = SharpDX.Direct3D11.DeviceContext;
using Factory = SharpDX.DXGI.Factory;
using FillMode = SharpDX.Direct3D11.FillMode;
using PixelShader = T3.Core.DataTypes.PixelShader;
using Resource = SharpDX.Direct3D11.Resource;
using ResourceManager = T3.Core.Resource.ResourceManager;
using Texture2D = T3.Core.DataTypes.Texture2D;
using VertexShader = T3.Core.DataTypes.VertexShader;

namespace T3.Player;
public partial class Program
{
    public class Options
    {
        [Option(Default = 0, Required = true, HelpText = "monitorHandle")]

        public int MonitorHandle { get; set; }
        [Option(Default = false, Required = false, HelpText = "Disable vsync")]
        public bool NoVsync { get; set; }

        [Option(Default = 1920, Required = false, HelpText = "Defines the width")]
        public int Width { get; set; }

        [Option(Default = 1080, Required = false, HelpText = "Defines the height")]
        public int Height { get; set; }

        [Option(Default = false, Required = false, HelpText = "Run in windowed mode")]
        public bool Windowed { get; set; }

        [Option(Default = false, Required = false, HelpText = "Loops the demo")]
        public bool Loop { get; set; }

        [Option(Default = true, Required = false, HelpText = "Show log messages.")]
        public bool Logging { get; set; }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        CoreUi.Instance = null;
        fileWriter = null;
        ShaderCompiler.ResetShaderCacheSubdirectory();
        CoreUi.Instance = new MsForms.MsForms();
        BlockingWindow.Instance = new SilkWindowProvider();
        exportSettings = null;
        ProjectSettings.Config = null;
        _resolvedOptions = new Options();
        //Application.EnableualStyles();
        //Application.SetHighDpiMode(HighDpiMode.PerMonitor);
        //Application.SetCompatibleTextRenderingDefault(false);
        if (_evalContext != null)
        {
        _evalContext.Reset();

        }
        //OSC Receiver
        //Initialisieren des OSC - Handlers
        _oscHandler = new OscMessageHandler();
        OscConnectionManager.IOscConsumer _ioscConsumer = null;
        // Registrieren des Handlers beim OscConnectionManager
        int oscPort = 8000; // Beispiel-Port
        OscConnectionManager.RegisterConsumer(_oscHandler, oscPort);

        Console.WriteLine($"OSC-Handler auf Port {oscPort} registriert.");



        //Adapterrating
        using var factory = new Factory1();

        if (factory.GetAdapterCount() == 0)
        {
            BlockingWindow.Instance.ShowMessageBox("We are unable to find any graphics adapters",
                                                   "Oh noooo",
                                                   "OK");
            Environment.Exit(0);
        }
        nint monitorHandle = 0; // Standardwert

        foreach (var arg in args)
        {
            if (arg.StartsWith("--monitorhandle="))
            {
                if (nint.TryParse(arg.Split('=')[1], out nint handle))
                {
                    monitorHandle = handle;
                }
            }
        }


        var adapterRatings = new List<DisplayAdapterRating>(8);

        for (var i = 0; i < factory.GetAdapterCount(); i++)
        {
            using var adapter = factory.GetAdapter1(i);
            const long gb = 1024 * 1024 * 1024;

            var newRating = new DisplayAdapterRating
            {
                Name = adapter.Description.Description,
                Index = i,
                MemoryInGb = (float)((double)adapter.Description.DedicatedVideoMemory / gb),
            };
            adapterRatings.Add(newRating);

            var descriptionLower = adapter.Description.Description.ToLowerInvariant();

            // Positive keywords
            foreach (var keyword in highPerformanceKeywords)
            {
                if (!descriptionLower.Contains(keyword))
                    continue;

                newRating.Rating *= 2f;
            }

            // Negative keywords
            foreach (var keyword in integratedKeywords)
            {
                if (!descriptionLower.Contains(keyword))
                    continue;

                newRating.Rating *= 0.2f;
            }

            var memSizeFactor = newRating.MemoryInGb switch
            {
                < 1 => 0.1f,
                < 2 => 0.5f,
                < 4 => 1f,
                < 8 => 2f,
                > 8 => 3f,
                _ => 4f
            };
            newRating.Rating *= memSizeFactor;
        }

        var selectedAdapterIndex = adapterRatings.OrderByDescending(r => r.Rating).First().Index;

        var selectedAdapter = factory.GetAdapter1(selectedAdapterIndex);
        ActiveGpu = selectedAdapter.Description.Description;




        var settingsPath = Path.Combine(FileLocations.StartFolder, "exportSettings.json");
        if (!JsonUtils.TryLoadingJson(settingsPath, out exportSettings))
        {
            var message = $"Failed to load export settings from \"{settingsPath}\". Exiting!";
            Log.Error(message);
            BlockingWindow.Instance.ShowMessageBox(message);
            return;
        }

        ProjectSettings.Config = exportSettings!.ConfigData;
            
        var logDirectory = Path.Combine(Core.UserData.FileLocations.SettingsDirectory, "Performanie" , exportSettings.Author, exportSettings.ApplicationTitle);
        if (fileWriter == null)
        {
            fileWriter = FileWriter.CreateDefault(logDirectory, out logPath);
        }
       consoleWriter = new ConsoleWriter();
        try
        {
            Log.AddWriter(consoleWriter);
            Log.AddWriter(fileWriter);


            if (!TryResolveOptions(args, exportSettings!, out _resolvedOptions))
                    return;
           
            Log.Debug("Resolved options: " + JsonConvert.SerializeObject(_resolvedOptions, Formatting.Indented));
            Log.Info($"Starting {exportSettings.ApplicationTitle} with id {exportSettings.OperatorId} by {exportSettings.Author}.");
            Log.Info($"Build: {exportSettings.BuildId}, Editor: {exportSettings.EditorVersion}");
                
            ShaderCompiler.ShaderCacheSubdirectory = Path.Combine("Player", 
                                                                  exportSettings.EditorVersion, 
                                                                  exportSettings.Author,
                                                                  exportSettings.ApplicationTitle, 
                                                                  exportSettings.OperatorId.ToString(), 
                                                                  exportSettings.BuildId.ToString());

            var resolution = new Int2(_resolvedOptions.Width, _resolvedOptions.Height);
            _vsyncInterval = Convert.ToInt16(!_resolvedOptions.NoVsync);
            Log.Debug($": {_vsyncInterval}, windowed: {_resolvedOptions.Windowed}, size: {resolution}, loop: {_resolvedOptions.Loop}, logging: {_resolvedOptions.Logging}");

            var iconPath = Path.Combine("images", "editor","t3.ico");
            var gotIcon = File.Exists(iconPath);

            Icon icon;
            if (!gotIcon)
            {
                Log.Warning("Failed to load icon");
                icon = null;
            }
            else
            {
                icon = new Icon(iconPath);
            }
            Rectangle monitorBounds = Rectangle.Empty;

            for (int adapterIndex = 0; adapterIndex < factory.GetAdapterCount1(); adapterIndex++)
            {
                using (var adapter = factory.GetAdapter1(adapterIndex))
                {
                    for (int outputIndex = 0; outputIndex < adapter.GetOutputCount(); outputIndex++)
                    {
                        using (var output = adapter.GetOutput(outputIndex))
                        {
                            if (output.Description.MonitorHandle == monitorHandle)
                            {
                                Console.WriteLine($"Monitor gefunden: {output.Description.DeviceName}");
                                monitorBounds = new Rectangle(
                                    output.Description.DesktopBounds.Left,
                                    output.Description.DesktopBounds.Top,
                                    output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left,
                                    output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top
                                );
                                break;
                            }
                        }
                    }
                }
            }

            //_renderForm = new RenderForm("Performanie Pro 3")
            //                  {
            //                      ClientSize = new Size(resolution.X, resolution.Y),
            //                      StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            //                      AllowUserResizing = false,
            //                      //Icon = icon,
            //                  };

            if (monitorBounds != Rectangle.Empty)
            {
                _renderForm = new RenderForm("Performanie Pro 3")
                {
                    //                      ClientSize = new Size(resolution.X, resolution.Y),
                    //                      StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    //                      AllowUserResizing = false,
                    //                      //Icon = icon,
                    //  
                    StartPosition = FormStartPosition.Manual,
                   FormBorderStyle = FormBorderStyle.Sizable,
                   WindowState = FormWindowState.Normal,
                    Location = new Point(monitorBounds.X, monitorBounds.Y),
                    ClientSize = new Size(
                    Math.Min(monitorBounds.Width, _resolvedOptions.Width),
                    Math.Min(monitorBounds.Height, _resolvedOptions.Height)),
                    //TopMost = true,

                };
                if (!_resolvedOptions.Windowed)
                {
                    _renderForm.FormBorderStyle = FormBorderStyle.None;
                    _renderForm.WindowState = FormWindowState.Maximized;
                    _renderForm.TopMost = true;
                }
            }
            else
            {
                Console.WriteLine("Kein Monitor mit dem angegebenen Handle gefunden. Standardposition wird verwendet.");
            }

            var windowHandle = _renderForm.Handle;

            // SwapChain description
            var desc = new SwapChainDescription
                           {
                               BufferCount = 2,
                               ModeDescription = new ModeDescription(resolution.X, resolution.Y,
                                                                     new Rational(60, 1), Format.R8G8B8A8_UNorm),
                               IsWindowed = true,
                               OutputHandle = windowHandle,
                               SampleDescription = new SampleDescription(1, 0),
                               SwapEffect = SwapEffect.FlipDiscard,
                               Flags = SwapChainFlags.AllowModeSwitch,
                               Usage = Usage.RenderTargetOutput,
                           };

            //Try to load 11.1 if possible, revert to 11.0 auto
            FeatureLevel[] levels =
{
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
            };

            // Create Device and SwapChain
#if DEBUG || FORCE_D3D_DEBUG
            var deviceCreationFlags = DeviceCreationFlags.Debug | DeviceCreationFlags.BgraSupport;
#else
                var deviceCreationFlags = DeviceCreationFlags.None;
#endif
            Device.CreateWithSwapChain(selectedAdapter, deviceCreationFlags, desc, out _device, out _swapChain);
           
                
            ResourceManager.Init(_device);
            _deviceContext = _device.ImmediateContext;

            

            var cursor = CoreUi.Instance.Cursor;

            if (_swapChain.IsFullScreen)
            {
                cursor.SetVisible(false);
            }

            // Ign ore all windows events
            var factoryend = _swapChain.GetParent<Factory>();
            factoryend.MakeWindowAssociation(_renderForm.Handle, WindowAssociationFlags.IgnoreAll);

            InitializeInput(_renderForm);

            // New RenderTargetView from the backbuffer
            _backBuffer = Resource.FromSwapChain<SharpDX.Direct3D11.Texture2D>(_swapChain, 0);
            _renderView = new RenderTargetView(_device, _backBuffer);

            //var shaderCompiler = new DX11ShaderCompiler
            //                         {
            //                             Device = _device
            //                         };
            //ShaderCompiler.Instance = shaderCompiler;
            if (ShaderCompiler.Instance == null)
            {
                ShaderCompiler.Instance = new DX11ShaderCompiler
                {
                    Device = _device
                };
            }

            SharedResources.Initialize();
                
            _fullScreenPixelShaderResource = SharedResources.FullScreenPixelShaderResource;
            _fullScreenVertexShaderResource = SharedResources.FullScreenVertexShaderResource;

            LoadOperators();

            if(!SymbolRegistry.TryGetSymbol(exportSettings.OperatorId, out var demoSymbol))
            {
                CloseApplication(true, $"Failed to find [{exportSettings.ApplicationTitle}] with id {exportSettings.OperatorId}");
                return;
            }

            Log.Debug($"Try to load playback settings for {demoSymbol}");
            var playbackSettings = demoSymbol.PlaybackSettings;
            if (playbackSettings != null)
            {
                Log.Debug("Playback settings: " + JsonConvert.SerializeObject(
                                                                              playbackSettings,
                                                                              Formatting.Indented
                                                                             ));
            }
            else
            {
                Log.Warning($"No playback settings defined");

            }
            
            _playback = new Playback
                            {
                                Settings = playbackSettings
                            };

            // Create instance of project op, all children are create automatically

            if (!demoSymbol.TryGetParentlessInstance(out _project))
            {
                CloseApplication(true, $"Failed to create instance of project op {demoSymbol}");
                return;
            }
            if (_evalContext == null)
            {
            _evalContext = new EvaluationContext();

            }

            var prerenderRequired = false;

            Bass.Free();
            Bass.Init();

            _resolution = new Int2(_resolvedOptions.Width, _resolvedOptions.Height);

            // Init wasapi input if required
            if (playbackSettings is { AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack } 
                && playbackSettings.TryGetMainSoundtrack(_project, out _soundtrackHandle))
            {
                //var soundtrack = _soundtrackHandle.Value;
                if (_soundtrackHandle.TryGetFileResource(out var file))
                {
                    _playback.Bpm = _soundtrackHandle.Clip.Bpm;
                    // Trigger loading clip
                    AudioEngine.UseAudioClip(_soundtrackHandle, 0);
                    AudioEngine.CompleteFrame(_playback, Playback.LastFrameDuration); // Initialize
                    prerenderRequired = true;
                }
                else
                {
                    Log.Warning($"Can't find soundtrack {_soundtrackHandle.Clip.FilePath}");
                    _soundtrackHandle = null;
                }
            }

            var rasterizerDesc = new RasterizerStateDescription
                                     {
                                         FillMode = FillMode.Solid,
                                         CullMode = CullMode.None,
                                         IsScissorEnabled = false,
                                         IsDepthClipEnabled = false
                                     };
            _rasterizerState = new RasterizerState(_device, rasterizerDesc);

            foreach (var output in _project.Outputs)
            {
                if (output is Slot<Texture2D> textureSlot)
                {
                    if (_textureOutput == null)
                        _textureOutput = textureSlot;
                    else
                    {
                        var message = "Multiple texture outputs found. Only the first one will be used.";
                        Log.Warning(message);
                        break;
                    }
                }
            }

            if (_textureOutput == null)
            {
                var sb = new StringBuilder();
                var slots = _project.Outputs.Where(x => x is not null).ToArray();
                sb.AppendLine("Found the following outputs:");
                foreach (var slot in slots)
                {
                    sb.AppendLine($"{slot.GetType()} | {slot.ValueType} ({slot.ValueType.Assembly.ToString()}\n");
                }

                sb.AppendLine();
                sb.AppendLine("Expected:");
                sb.Append($"{typeof(Slot<Texture2D>).FullName} | {typeof(Texture2D).FullName} ({typeof(Texture2D).Assembly.ToString()}\n");
                var message = $"Failed to find texture output. \n{sb}";
                CloseApplication(true, message);
                return;
            }

            // TODO - implement proper shader pre-compilation as an option to instance instantiation
            // move this to core?
            // Sample some frames to preload all shaders and resources
            if (prerenderRequired)
            {
                PreloadShadersAndResources(_soundtrackHandle.Clip.LengthInSeconds, _resolution, _playback, _deviceContext, _evalContext, _textureOutput, _swapChain,
                                           _renderView);
            }

            // Start playback           
            _playback.Update();
            _playback.TimeInBars = 0;
            _playback.PlaybackSpeed = 1.0;

            _renderForm.FormClosing += (sender, e) =>
            {
                if (sender == _renderForm)
                {
                    //_deviceContext?.ClearState();
                    //_deviceContext?.Flush();
                   
                    //_deviceContext?.Dispose();
                    CloseApplication(false, "Das Hauptfenster wurde durch das Schließen-Symbol geschlossen.");
                }
                else
                {
                    // Verhindere, dass andere Fenster geschlossen werden
                    e.Cancel = true;
                }

            };

            _renderForm.KeyDown += (sender, e) =>
            {
                // Anwendung schließen, wenn die Escape-Taste gedrückt wird
                if (e.KeyCode == System.Windows.Forms.Keys.Escape && sender == _renderForm)
                {
                    //_deviceContext?.ClearState();
                    //_deviceContext?.Flush();
                    _renderForm.Close();
                    
                    
                }
            };

            try
            {
                // Main loop
                RenderLoop.Run(_renderForm, RenderCallback);
            }
            catch (TimelineEndedException)
            {
                Log.Info($"Program ended at the end of the timeline: {_playback.TimeInSecs:0.00}s / {_playback.TimeInBars:0.00} bars");
                CloseApplication(false, null);
            }
            catch (Exception e)
            {
                var errorMessage = "Exception in main loop:\n" + e;
                CloseApplication(true, errorMessage);
                Log.Error(errorMessage);
                fileWriter.Dispose(); // flush and close
                BlockingWindow.Instance.ShowMessageBox(errorMessage);
            }

        }
        catch (Exception e)
        {
            CloseApplication(true, "Exception in initialization:\n" + e);
        }
            
        return;

        

        void CloseApplication(bool error, string message)
        {
            close = true;

            //Log.Debug("Closing application");
            CoreUi.Instance.Cursor.SetVisible(true);
            //ShaderCompiler.Shutdown();
            bool openLogs = false;
                
            if (!string.IsNullOrWhiteSpace(message))
            {
                if (error)
                    Log.Error(message);
                else
                    Log.Info(message);

                const int maxLines = 10;
                message = StringUtils.TrimStringToLineCount(message, maxLines).ToString();

                if (error)
                {
                    message += "\n\nDo you want to open the log file?";

                    var result = BlockingWindow.Instance.ShowMessageBox(message, $"{exportSettings.ApplicationTitle} crashed /:", "Yes", "No");
                    openLogs = result == "Yes";
                }
            }
            Log.RemoveWriter(fileWriter);
            Log.RemoveWriter(consoleWriter);
            fileWriter.Dispose();


            // Release all resources
            try
            {
                args.ToList().Clear();
                exportSettings = null;
                _fullScreenPixelShaderResource.Dispose();
                _fullScreenPixelShaderResource = null;
                _fullScreenVertexShaderResource.Dispose();
                _fullScreenVertexShaderResource = null;
                _rasterizerState.Dispose();
                _rasterizerState = null;
                _outputTexture.Dispose();
                _outputTexture = null;
                _outputTextureSrv.Dispose();
                _outputTextureSrv = null;
                //_evalContext.Reset();
                _evalContext = null;
                _project = null;
                _playback = null;
                _textureOutput = null;
                
                //SharedResources.Dispose();
                ShaderCompiler.Shutdown();
                ResourceManager.DefaultSamplerState.Dispose();
            DefaultRenderingStates._defaultBlendState = null;
                DefaultRenderingStates._disabledBlendState = null;
                DefaultRenderingStates._defaultDepthStencilState = null;
                DefaultRenderingStates._disabledDepthStencilState = null;
                _swapChain.Dispose();
                _swapChain = null;
                _renderView?.Dispose();
                _renderView = null;
                _backBuffer?.Dispose();
                _backBuffer = null;
                //_evalContext = null;
                _deviceContext?.ClearState();
                _deviceContext?.Flush();
                _deviceContext?.Dispose();
                //_device?.Dispose();
                //_device = null;

                //OscConnectionManager.UnregisterConsumer(_oscHandler);

                //_oscHandler = null;

                //Console.WriteLine("OSC-Handler abgemeldet.");
                Application.ExitThread();
                Log.Debug("Disposed of D3D resources");
               
            }
            catch (Exception e)
            {
                Log.Error($"Failed to dispose of resources: {e}");
            }

            if (openLogs)
            {
                CoreUi.Instance.OpenWithDefaultApplication(logPath);
            }
            
            //CoreUi.Instance.Shutdown();
            //CoreUi.Instance.ExitApplication();
        }
    }

    private static void RebuildBackBuffer(RenderForm form, Device device, ref RenderTargetView rtv, ref SharpDX.Direct3D11.Texture2D buffer, SwapChain swapChain)
    {
        rtv.Dispose();
        buffer.Dispose();
        swapChain.ResizeBuffers(3, form.ClientSize.Width, form.ClientSize.Height, Format.Unknown, SwapChainFlags.AllowModeSwitch);
        buffer = Resource.FromSwapChain<SharpDX.Direct3D11.Texture2D>(swapChain, 0);
        rtv = new RenderTargetView(device, buffer);
    }

    private static bool TryResolveOptions(string[] args, ExportSettings exportSettings, out Options resolvedOptions)
    {

        var parser = new Parser(config =>
                                {
                                    config.HelpWriter = null;
                                    config.AutoVersion = false;
                                });
        var parserResult = parser.ParseArguments<Options>(args);
        var helpText = HelpText.AutoBuild(parserResult,
                                          h =>
                                          {
                                              h.AdditionalNewLineAfterOption = false;

                                              // Todo: This should use information from the main operator
                                              h.Heading = exportSettings.ApplicationTitle;

                                              h.Copyright = exportSettings.Author;
                                              h.AutoVersion = false;
                                              return h;
                                          },
                                          e => e);

        Options parsedOptions = null;
        parserResult.WithParsed(o => { parsedOptions = o; })
                    .WithNotParsed(_ => { Log.Debug(helpText); });

        resolvedOptions = parsedOptions;
        
        if (resolvedOptions == null)
            return false;
            
        // use windowed status _only_ when explicitly set, the Options struct doesn't know about this
        if (!args.Any(s => "--windowed".Contains(s)))
        {
            parsedOptions.Windowed = exportSettings.WindowMode == WindowMode.Windowed;
        }

        return true;
    }

    private readonly struct PackageLoadInfo(
        PlayerSymbolPackage package,
        List<SymbolJson.SymbolReadResult> newlyLoadedSymbols)
    {
        public readonly PlayerSymbolPackage Package = package;
        public readonly List<SymbolJson.SymbolReadResult> NewlyLoadedSymbols = newlyLoadedSymbols;
    }

    // Private static bool _inResize;
    private static int _vsyncInterval;
    private static SwapChain _swapChain;
    private static RenderTargetView _renderView;
    private static SharpDX.Direct3D11.Texture2D _backBuffer;
    private static Instance _project;
    private static EvaluationContext _evalContext;
    private static Playback _playback;
    private static AudioClipResourceHandle _soundtrackHandle;
    private static DeviceContext _deviceContext;
    private static Options _resolvedOptions;
    private static RenderForm _renderForm;
    private static Texture2D _outputTexture;
    private static ShaderResourceView _outputTextureSrv;
    private static RasterizerState _rasterizerState;
    private static Resource<VertexShader> _fullScreenVertexShaderResource;
    private static Resource<PixelShader> _fullScreenPixelShaderResource;
    private static Device _device;
    private static Int2 _resolution;
    private static Slot<Texture2D> _textureOutput;
    private static T3.Core.Logging.ILogWriter fileWriter;
    private static string logPath;
    private static ExportSettings exportSettings;
    public static bool close = false;
    private static ConsoleWriter consoleWriter;

    //Adaptergrafik
    private static string[] highPerformanceKeywords = ["dedicated", "high performance", "rtx", "gtx"];
    private static string[] integratedKeywords = ["integrated", "intel(r) uhd graphics", "microsoft basic render", "microsoft basic render"]; // twice to make MS worse
    private static nint monitorHandle;
    
    private sealed class DisplayAdapterRating()
    {
        public string Name;
        public int Index;
        public float MemoryInGb = 0;
        public float Rating = 1;
    }
    public static string ActiveGpu { get; private set; } = "Unknown";
    public class OscMessageHandler : OscConnectionManager.IOscConsumer
    {
        public void ProcessMessage(OscMessage msg)
        {
            // Verarbeiten der empfangenen OSC-Nachricht
            Console.WriteLine($"Empfangene OSC-Nachricht: {msg.Address}");
            foreach (var arg in msg)
            {
                Console.WriteLine($"Argument: {arg}");
            }
        }
    }
    private static OscMessageHandler _oscHandler;
}
