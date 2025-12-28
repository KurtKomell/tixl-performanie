// NOTE: Enabling this will require Windows Graphics Tools feature to be enabled
// This will prevent the player from running on most Windows systems.
#define FORCE_D3D_DEBUG
using CommandLine;
using CommandLine.Text;
using ManagedBass;
using Microsoft.CodeAnalysis;
using Microsoft.VisualBasic.Devices;
using Newtonsoft.Json;
using NuGet.Configuration;
using Operators.Utils;
using Rug.Osc;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Windows;
using Silk.NET.Core.Contexts;
using Silk.NET.GLFW;
using SilkWindows;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.Compilation;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.IO;
using T3.Core.Logging;
using T3.Core.Model;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Rendering;
using T3.Core.Rendering.Material;
using T3.Core.Resource;
using T3.Core.SystemUi;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Serialization;
using static T3.Core.Rendering.FogSettings;
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

        [Option(Default = "", Required = false, HelpText = "Audiodevice")]
        public string Audio { get; set; }
    }

    [STAThread]
    public void Main(string[] args)
    {
        //Application.EnableVisualStyles();
        //Application.SetHighDpiMode(HighDpiMode.PerMonitor);
        //Application.SetCompatibleTextRenderingDefault(false);
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
       
        foreach (var arg in args)
        {
            if (arg.StartsWith("--audio="))
            {
                if (int.TryParse(arg.Split('=')[1], out int audioDeviceArg))
                {
                    audioDeviceIndex = audioDeviceArg;
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

        selectedAdapter = factory.GetAdapter1(selectedAdapterIndex);
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
            Log.Debug($": audio={audioDeviceIndex},  {_vsyncInterval}, windowed: {_resolvedOptions.Windowed}, size: {resolution}, loop: {_resolvedOptions.Loop}, logging: {_resolvedOptions.Logging}");

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
            SharpDX.Rectangle monitorBounds = SharpDX.Rectangle.Empty;

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
                                monitorBounds = new SharpDX.Rectangle(
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

            if (monitorBounds != SharpDX.Rectangle.Empty)
            {
                _renderForm = new RenderForm("Performanie Pro 3 | Program Window")
                {
                    //                      ClientSize = new Size(resolution.X, resolution.Y),
                    //                      StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    //                      AllowUserResizing = false,
                    //                      //Icon = icon,
                    //  
                    StartPosition = FormStartPosition.Manual,
                   FormBorderStyle = FormBorderStyle.Sizable,
                   WindowState = FormWindowState.Normal,
                    Location = new System.Drawing.Point(monitorBounds.X, monitorBounds.Y),
                    ClientSize = new Size(
                    Math.Min(monitorBounds.Width, _resolvedOptions.Width),
                    Math.Min(monitorBounds.Height, _resolvedOptions.Height)),
                    //TopMost = true,

                };
                if (!_resolvedOptions.Windowed)
                {
                    _renderForm.FormBorderStyle = FormBorderStyle.None;
                    _renderForm.WindowState = FormWindowState.Maximized;
                    //_renderForm.TopMost = true;
                }
            }
            else
            {
                Console.WriteLine("Kein Monitor mit dem angegebenen Handle gefunden. Standardposition wird verwendet.");
            }
            //_renderForm.Resize += RenderForm_Resize;
            var windowHandle = _renderForm.Handle;

            // SwapChain description
            var desc = new SwapChainDescription
                           {
                               BufferCount = 3,
                               ModeDescription = new ModeDescription(resolution.X, resolution.Y,
                                                                     new Rational(60, 1), Format.R8G8B8A8_UNorm),
                               IsWindowed = true,
                               OutputHandle = windowHandle,
                               SampleDescription = new SampleDescription(1, 0),
                               SwapEffect = SwapEffect.FlipDiscard,
                               Flags = SwapChainFlags.None,
                               Usage = Usage.RenderTargetOutput | Usage.ShaderInput,
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
            //_deviceContext.VertexShader.GetConstantBuffers(0, 3);
            //_deviceContext.PixelShader.GetConstantBuffers(0, 3);


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

            if(!SymbolRegistry.TryGetSymbol(exportSettings.OperatorId, out demoSymbol))
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

            };
            

            _playback = new Playback
                            {
                                Settings = playbackSettings
                                
            };

            for (int i = 0; i < WasapiAudioInput._inputDevices.Count(); i++)
            {
                if (i == audioDeviceIndex)
                {


                    audioDevice = WasapiAudioInput._inputDevices[i].DeviceInfo.Name;
                    break;
                }
            }
            
            _playback.Settings.AudioInputDeviceName = audioDevice;

            // Create instance of project op, all children are create automatically

            if (!demoSymbol.TryGetParentlessInstance(out _project))
            {
                CloseApplication(true, $"Failed to create instance of project op {demoSymbol}");
                return;
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

            if (_outputTextureSrv == null)
            {
                Log.Debug("Creating new srv...");
                _outputTextureSrv = new ShaderResourceView(_device, _backBuffer);
            }
           
            //Guid spoutGuid = Guid.Parse("13be1e3f-861d-4350-a94e-e083637b3e55");
            //var spoutOutputInstance = _project.Children.FirstOrDefault(child => child.Value.Symbol.Id == spoutGuid);
            



                
            _evalContext = new EvaluationContext();
            _evalContext.RequestedResolution = _resolution;
            //_evalContext.PointLights.Clear();
            //_evalContext.PointLights.GetDefaultBuffer();

            // Beispiel: PbrMaterial-Instanz erstellen
            // Überprüfen, ob _evalContext und PbrMaterial initialisiert sind
            //_evalContext.PbrMaterial.AlbedoMapSrv = PbrMaterial.DefaultAlbedoColorSrv;
            //_evalContext.PbrMaterial.EmissiveMapSrv = PbrMaterial.DefaultEmissiveColorSrv;
            //_evalContext.PbrMaterial.RoughnessMetallicOcclusionSrv = PbrMaterial.DefaultRoughnessMetallicOcclusionSrv;
            //_evalContext.PbrMaterial.NormalSrv = PbrMaterial.DefaultNormalSrv;

            //_evalContext.PbrMaterial.Parameters = new PbrMaterial.PbrParameters
            //{
            //    BaseColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
            //    EmissiveColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            //    Roughness = 1.0f,
            //    Specular = 0.5f,
            //    Metal = 0.0f
            //};
            //PbrContextSettings.SetDefaultToContext(_evalContext);

            ////_evalContext.PbrMaterial = PbrMaterial.Set();

            //// Texturen neu zuweisen
            //_evalContext.ContextTextures = new Dictionary<string, Texture2D>();

            //// Konstanten neu initialisieren
            //_evalContext.FogParameters = FogSettings.ResetDefaultSettingsBuffer();


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
                // Überprüfen, ob die Taste bereits verarbeitet wurde
                if (!_processedKeys.Contains(e.KeyCode))
                {
                    // Taste als verarbeitet markieren
                    _processedKeys.Add(e.KeyCode);

                    // Setze den Wert von keyInPlayer
                    keyInPlayer = e.KeyCode.ToString();

                    // Anwendung schließen, wenn die Escape-Taste gedrückt wird
                    if (e.Control && e.KeyCode == System.Windows.Forms.Keys.F && sender == _renderForm)
                    {
                        if (!_resolvedOptions.Windowed)
                        {
                            _resolvedOptions.Windowed = true;
                            SwitchToMonitor((nint)_resolvedOptions.MonitorHandle, _resolvedOptions.Windowed, _resolvedOptions.Width, _resolvedOptions.Height, _swapChain);
                        }
                        else
                        {
                            _resolvedOptions.Windowed = false;
                            SwitchToMonitor((nint)_resolvedOptions.MonitorHandle, _resolvedOptions.Windowed, _resolvedOptions.Width, _resolvedOptions.Height, _swapChain);
                        }
                    }
                         
                    
                }
            };

            _renderForm.KeyUp += (sender, e) =>
            {
                // Entferne die Taste aus der Liste der verarbeiteten Tasten, wenn sie losgelassen wird
                _processedKeys.Remove(e.KeyCode);
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
            fileWriter.Dispose();


            // Release all resources
            try
            {
                args.ToList().Clear();
             
                exportSettings = null;
                Guid spoutGuid = Guid.Parse("13be1e3f-861d-4350-a94e-e083637b3e55");
                var spoutSymbolChild = _project.Children.SingleOrDefault(child => child.Value.Symbol.Id == spoutGuid);
                if (spoutSymbolChild.Value != null)
                {
                    // Die Instanz selbst holen und deren Dispose-Methode aufrufen
                    spoutSymbolChild.Value.Symbol.Dispose();
                    Console.WriteLine("Disposed SpoutOutput instance and its resources.");
                }

                //var spoutOutputInstance = _project.Children.FirstOrDefault(child => child.Value.Symbol.Id == spoutGuid);
                //if (spoutOutputInstance.Value != null)
                //{
                //    // Die `Value` sollte die Instanz der Klasse sein, die Spout enthält
                //    var spoutObject = spoutOutputInstance.Value.Symbol;
                //    foreach (var child in spoutObject.Children)
                //    {
                //        foreach (var instance in child.Value.Symbol.InstancesOfSelf)
                //        {
                //            instance.Symbol.Dispose();
                //        }
                //        child.Value.Symbol.Dispose();
                //    }

                //    // ZUERST: Geben Sie das SpoutOutput-Objekt selbst frei
                //    spoutObject.Dispose();
                //    spoutObject = null;
                //    Console.WriteLine("Disposed SpoutOutput Symbol");

                //    // ZWEITENS (optional, aber gut): Geben Sie die Kinder frei
                //}
                _fullScreenPixelShaderResource.Value.Dispose();
                _fullScreenPixelShaderResource.Dispose();
                _fullScreenPixelShaderResource = null;
                _fullScreenVertexShaderResource.Value?.Dispose();
                _fullScreenVertexShaderResource.Dispose();
                _fullScreenVertexShaderResource = null;
                _rasterizerState.Dispose();
                _rasterizerState = null;
                _outputTexture.Dispose();
                _outputTexture = null;
                _outputTextureSrv.Resource.Dispose();
                //Utilities.Dispose(ref _outputTextureSrv);
                _outputTextureSrv.Dispose();
                _outputTextureSrv = null;



                _project = null;
                _playback = null;
                _textureOutput = null;
                
                //SharedResources.Dispose();

                ShaderCompiler.Shutdown();
                ShaderCompiler.Instance = null;
                ResourceManager.DefaultSamplerState.Dispose();
                
                SharedResources.Dispose();
                _evalContext.PointLights.Clear();
                _evalContext.PbrMaterial.Dispose();
                _evalContext.PbrMaterial = null;
                _evalContext.FloatVariables.Clear();
                _evalContext.ContextTextures.Clear();
                _evalContext.FogParameters.Dispose();
                _evalContext.FogParameters = null;
                _evalContext.BoolVariables.Clear();
                _evalContext.IntVariables.Clear();
                _evalContext.ObjectVariables.Clear();

        
                //foreach (var symbol in demoSymbol.SymbolPackage.Symbols.Values)
                //{
                //        if (symbol.InstanceType.Name == "Bubbles")
                //        {
                //        foreach (var child in symbol.Children)
                //        {
                //            //Console.WriteLine(child.Value);
                //            if (child.Value.Symbol.InstanceType.Name == "DrawMeshAtPoints")
                //            {
                //                foreach (var instance in child.Value.Symbol.InstancesOfSelf)
                //                {
                //                    foreach (var childsymbol in instance.Symbol.Children.Values)
                //                    {
                //                        //Console.WriteLine(childsymbol.ReadableName);
                                      
                //                        {
                //                            foreach (var inst in childsymbol.Instances)
                //                            {
                //                                Console.WriteLine(inst.Symbol.InstanceType.Name);
                                               
                //                                    inst.Symbol.Dispose();
                //                                    //Console.WriteLine("Disposed MeshBuffers instance");
                                                
                //                            }
                //                           //if (childsymbol.IsDisabled)
                //                           // {
                //                           //     Console.WriteLine("Disposed disabled childsymbol");
                //                           // }
                //                            //if (childinst.Value.Symbol.InstanceType.Name == "MeshBuffers")
                //                            //{
                //                            //    foreach (var meshInstance in childinst.Value.Symbol.InstancesOfSelf)
                //                            //    {
                //                            //        //if (meshInstance is MeshBuffers meshBuffers)
                //                            //        //{
                //                            //        //    meshBuffers.Dispose();
                //                            //        //    Console.WriteLine("Disposed MeshBuffers instance");
                //                            //        //}
                //                            //    }
                //                            //}
                //                        }
                //                    }
                //                }
                //            }
                //        }

                //        }// Erstelle eine Instanz von SceneSetup oder führe die gewünschte Aktion aus
                //            //var sceneSetup = (MeshBuffers)symbol.InstancesOfSelf;
                //            //sceneSetup.Dispose();
                //            //Console.WriteLine($"Disposed SceneSetup for child with ID: {child.Key}");
                        
                    
                //}
                demoSymbol.SymbolPackage.Dispose();
                demoSymbol.Dispose();
                





                _evalContext = null;


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
                _evalContext = null;
                // ConstantBuffer für VertexShader zurücksetzen
                for (int i = 0; i < 14; i++)
                {
                    _deviceContext.VertexShader.SetConstantBuffer(i, null);
                    _deviceContext.PixelShader.SetConstantBuffer(i, null);
                }
                
                
                IterateConstantBufferSlots(_deviceContext);
                //_deviceContext.ComputeShader.Dispose();
                //_deviceContext.ComputeShader.Set(null);
                //_deviceContext.DomainShader.Dispose();
                //_deviceContext.DomainShader.Set(null);
                //_deviceContext.HullShader.Dispose();
                //_deviceContext.HullShader.Set(null);
                //_deviceContext.GeometryShader.Dispose();
                //_deviceContext.GeometryShader.Set(null);
                //_deviceContext.PixelShader.Dispose();
                //_deviceContext.PixelShader.Set(null);
                //_deviceContext.VertexShader.Dispose();
                //_deviceContext.VertexShader.Set(null);
                _deviceContext?.ClearState();
                _deviceContext?.Flush();
                _deviceContext?.Dispose();
                _deviceContext = null;
                
                //_device?.Dispose();
                //_device = null;

                //OscConnectionManager.UnregisterConsumer(_oscHandler);

                //_oscHandler = null;

                //Console.WriteLine("OSC-Handler abgemeldet.");

                Log.Debug("Disposed of D3D resources");
            Log.RemoveWriter(consoleWriter);
                Application.ExitThread();
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
            Environment.Exit(0);
        }
    }



    private static void RenderForm_Resize(object sender, EventArgs e)
    {
        if (_swapChain == null || _renderForm.ClientSize.Width == 0 || _renderForm.ClientSize.Height == 0)
            return;

        // Ressourcen freigeben
        //_outputTexture.Dispose();

        _renderView?.Dispose();
        _outputTextureSrv?.Dispose();
        _backBuffer?.Dispose();
        _textureOutput.Invalidate();
        _deviceContext.OutputMerger.SetTargets((RenderTargetView)null);
        // Puffer der SwapChain an die neue Größe anpassen
        _swapChain.ResizeBuffers(3, _renderForm.ClientSize.Width, _renderForm.ClientSize.Height, Format.R8G8B8A8_UNorm, SwapChainFlags.AllowModeSwitch);

        // Neue Ressourcen aus der SwapChain erstellen
        _backBuffer = Resource.FromSwapChain<SharpDX.Direct3D11.Texture2D>(_swapChain, 0);
        _renderView = new RenderTargetView(_device, _backBuffer);

        // Optional: Shader-Ressourcenansicht für den Backbuffer neu erstellen, falls verwendet
        _outputTextureSrv = new ShaderResourceView(_device, _backBuffer);

        Log.Debug($"Resized backbuffer to {_renderForm.ClientSize.Width}x{_renderForm.ClientSize.Height}");
    }

    public void Dispose()
    {
        if (_renderForm.InvokeRequired)
        {
            _renderForm.Invoke((Action)(() => _renderForm.Close()));
        }
        else
        {
            _renderForm.Close();
        }
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

    private static void IterateConstantBufferSlots(DeviceContext deviceContext)
    {
         // Maximale Anzahl der Slots (abhängig von der GPU, z. B. 14 für DirectX 11)

        
            // Hole den Constant Buffer für den aktuellen Slot
            var constantBuffers = deviceContext.VertexShader.GetConstantBuffers(0, 14);
        foreach (SharpDX.Direct3D11.Buffer constantBuffer in constantBuffers)
        {
            if (constantBuffer != null)
            {
                
                // Hier kannst du weitere Informationen über den Buffer abrufen
                constantBuffer.Dispose(); // Optional: Dispose, wenn nicht mehr benötigt
              
            }
           
        }
        var pixelConstantBuffers = deviceContext.PixelShader.GetConstantBuffers(0, 14);
        foreach (SharpDX.Direct3D11.Buffer constantBuffer in pixelConstantBuffers)
        {
            if (constantBuffer != null)
            {
                
                // Hier kannst du weitere Informationen über den Buffer abrufen
                constantBuffer.Dispose(); // Optional: Dispose, wenn nicht mehr benötigt
                
            }

        }
    }

    private static void SwitchToMonitor(nint monitorHandle, bool windowed, int width, int height, SwapChain swapChain)
    {
        bool windowed2 = windowed;
        if (_renderForm == null)
            return;

        using var factory = new Factory1();
        

        for (int adapterIndex = 0; adapterIndex < factory.GetAdapterCount1(); adapterIndex++)
        {
            using var adapter = factory.GetAdapter1(adapterIndex);
            for (int outputIndex = 0; outputIndex < adapter.GetOutputCount(); outputIndex++)
            {
                using var output = adapter.GetOutput(outputIndex);
                if (output.Description.MonitorHandle == monitorHandle)
                {
                    monitorBounds = new SharpDX.Rectangle(
                                                  output.Description.DesktopBounds.Left,
                                                  output.Description.DesktopBounds.Top,
                                                  output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left,
                                                  output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top
                                                 );
                    goto FoundMonitor;
                }
            }
        }

    FoundMonitor:
        if (monitorBounds == SharpDX.Rectangle.Empty)
        {
            Log.Warning($"Could not find monitor with handle {monitorHandle}.");
            return;
        }

        Log.Debug($"Switching to monitor {monitorHandle} at {monitorBounds.Left}.");

        // Wichtig: UI-Änderungen müssen im UI-Thread ausgeführt werden.
        _renderForm.Invoke(new Action(() =>
        {
            if (windowed2)
            {
                _renderForm.WindowState = FormWindowState.Normal;
                _renderForm.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                _renderForm.Location = new System.Drawing.Point(monitorBounds.X, monitorBounds.Y);
                //_renderForm.Size = new Size(width, height);
                _renderForm.ClientSize = new Size(width, height);
            }
            else
            {
                // Um den Vollbildmodus auf einem anderen Monitor zu erzwingen,
                // müssen wir das Fenster zuerst in den normalen Zustand versetzen.
                _renderForm.WindowState = FormWindowState.Normal;
                _renderForm.FormBorderStyle = FormBorderStyle.None;
                _renderForm.Location = new System.Drawing.Point(monitorBounds.X, monitorBounds.Y);
                //_renderForm.Size = new Size(monitorBounds.Width, monitorBounds.Height);
                _renderForm.ClientSize = new Size(monitorBounds.Width, monitorBounds.Height);
                _renderForm.WindowState = FormWindowState.Maximized;
            }

           
            
        }));
    }
    private static void SwitchAudioInputDevice(int deviceIndex)
    {
        if (_playback == null)
        {
            Log.Warning("Playback not initialized. Cannot switch audio device.");
            return;
        }

        string newDeviceName = null;
        if (deviceIndex >= 0 && deviceIndex < WasapiAudioInput._inputDevices.Count)
        {
            newDeviceName = WasapiAudioInput._inputDevices[deviceIndex].DeviceInfo.Name;
            Log.Debug($"Attempting to switch audio input to index {deviceIndex}: '{newDeviceName}'");
        }
        else
        {
            Log.Warning($"Invalid audio device index received: {deviceIndex}. Not switching.");
            return;
        }

        if (_playback.Settings.AudioInputDeviceName == newDeviceName)
        {
            Log.Debug($"Audio device '{newDeviceName}' is already active.");
            return;
        }

        _playback.Settings.AudioInputDeviceName = newDeviceName;
        //Bass.Free();
        //Bass.Init();

        

        // We need to re-initialize the audio input to apply the change.
        AudioEngine.ReinitializeAudioInput();
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
    private static Symbol demoSymbol;
    private static SharpDX.Direct3D11.Buffer constBuffer;
    public string keyInPlayer;
    private static HashSet<System.Windows.Forms.Keys> _processedKeys = new HashSet<System.Windows.Forms.Keys>();
    private static SharpDX.Rectangle monitorBounds = SharpDX.Rectangle.Empty;

    //Adaptergrafik
    private static string[] highPerformanceKeywords = ["dedicated", "high performance", "rtx", "gtx"];
    private static string[] integratedKeywords = ["integrated", "intel(r) uhd graphics", "microsoft basic render", "microsoft basic render"]; // twice to make MS worse
    private static nint monitorHandle;
    private static int audioDeviceIndex;
    private static string audioDevice = "Default";
    private static SharpDX.DXGI.Adapter1 selectedAdapter;

    private sealed class DisplayAdapterRating()
    {
        public string Name;
        public int Index;
        public float MemoryInGb = 0;
        public float Rating = 1;
    }
    public static string ActiveGpu { get; private set; } = "Unknown";
    public static bool renderStarted = false;
    public class OscMessageHandler : OscConnectionManager.IOscConsumer
    {
        public void ProcessMessage(OscMessage msg)
        {
            // Verarbeiten der empfangenen OSC-Nachricht
            //Console.WriteLine($"Empfangene OSC-Nachricht: {msg.Address}");
            switch (msg.Address)
            {
                case "/performanie/monitorHandle":
                    if (msg.Count > 0 && msg[0] is string monitorHandleStr && nint.TryParse(monitorHandleStr, out nint handle))
                    {
                        _resolvedOptions.MonitorHandle = (int)handle;
                        

                        SwitchToMonitor(handle, _resolvedOptions.Windowed, _resolvedOptions.Width, _resolvedOptions.Height, _swapChain);
                    }
                    else
                    {
                        Log.Warning($"Invalid argument for /performanie/monitorhandle: {msg[0]}");
                    }
                    break;

                case "/performanie/windowed":
                    if (msg.Count > 0 && msg[0] is string windowedStr && bool.TryParse(windowedStr, out bool isWindowed))
                    {
                        _resolvedOptions.Windowed = isWindowed;
                        
                        SwitchToMonitor((nint)_resolvedOptions.MonitorHandle, _resolvedOptions.Windowed, _resolvedOptions.Width, _resolvedOptions.Height, _swapChain);
                    }
                    else
                    {
                        Log.Warning($"Invalid argument for /performanie/windowed: {msg[0]}");
                    }
                    break;

                case "/performanie/audiodevice":
                    if (msg.Count > 0 && msg[0] is string audioIndexStr && int.TryParse(audioIndexStr, out int audioIndex))
                    {
                        audioDeviceIndex = audioIndex;
                        SwitchAudioInputDevice(audioIndex);
                    }
                    else
                    {
                        Log.Warning($"Invalid argument for /performanie/audioinput: {msg[0]}");
                    }
                    break;
                case "/performanie/resolution":
                    if (msg.Count > 1 && msg[0] is string widthStrRes && int.TryParse(widthStrRes, out int newWidth) &&
                        msg[1] is string heightStrRes && int.TryParse(heightStrRes, out int newHeight))
                    {
                        _resolvedOptions.Width = newWidth;
                        _resolution.X = newWidth;
                        _resolvedOptions.Height = newHeight;
                        _resolution.Y = newHeight;

                        SwitchToMonitor((nint)_resolvedOptions.MonitorHandle, _resolvedOptions.Windowed, newWidth, newHeight, _swapChain);
                    }
                    else
                    {
                        Log.Warning($"Invalid arguments for /performanie/resolution. Expected two integer strings.");
                    }
                    break;
            }
        }
    }
    private static OscMessageHandler _oscHandler;
};
    
