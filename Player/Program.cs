// NOTE: Enabling this will require Windows Graphics Tools feature to be enabled
// This will prevent the player from running on most Windows systems.
//#define FORCE_D3D_DEBUG
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
using System.Net;
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
            
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Performanie", exportSettings.Author, exportSettings.ApplicationTitle);
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

            var iconPath = Path.Combine(FileLocations.StartFolder, "Images", "editor", "performanie.ico");
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

            if (!_resolvedOptions.Windowed && monitorBounds != SharpDX.Rectangle.Empty)
            {
                resolution = new Int2(monitorBounds.Width, monitorBounds.Height);
                _resolvedOptions.Width = monitorBounds.Width;
                _resolvedOptions.Height = monitorBounds.Height;
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
                    StartPosition = FormStartPosition.Manual,
                    FormBorderStyle = FormBorderStyle.Sizable,
                    WindowState = FormWindowState.Normal,
                    Icon = icon,
                    Location = new System.Drawing.Point(monitorBounds.X, monitorBounds.Y),
                    ClientSize = new Size(_resolvedOptions.Width, _resolvedOptions.Height),
                };

                if (!_resolvedOptions.Windowed)
                    ApplyBorderlessMonitorBounds(monitorBounds);
            }
            else
            {
                Console.WriteLine("Kein Monitor mit dem angegebenen Handle gefunden. Standardposition wird verwendet.");
            }
            _renderForm.ResizeBegin += (_, _) => _suppressFormResize = true;
            _renderForm.ResizeEnd += (_, _) =>
            {
                _suppressFormResize = false;
                RequestSwapChainResize();
            };
            _renderForm.Resize += RenderForm_Resize;
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
//            FeatureLevel[] levels =
//{
//                FeatureLevel.Level_11_1,
//                FeatureLevel.Level_11_0,
//            };  

            // Create Device and SwapChain
            //#if DEBUG || FORCE_D3D_DEBUG
            //            var deviceCreationFlags = DeviceCreationFlags.Debug | DeviceCreationFlags.BgraSupport;
            //#else
            var deviceCreationFlags = DeviceCreationFlags.None;
            //#endif
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
                        _resolvedOptions.Windowed = !_resolvedOptions.Windowed;
                        ApplyDisplaySettings(
                            _resolvedOptions.Windowed,
                            (nint)_resolvedOptions.MonitorHandle,
                            _resolvedOptions.Width,
                            _resolvedOptions.Height);
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
                //Guid spoutGuid = Guid.Parse("13be1e3f-861d-4350-a94e-e083637b3e55");
                //var spoutSymbolChild = _project.Children.SingleOrDefault(child => child.Value.Symbol.Id == spoutGuid);
                //if (spoutSymbolChild.Value != null)
                //{
                //    // Die Instanz selbst holen und deren Dispose-Methode aufrufen
                //    spoutSymbolChild.Value.Symbol.Dispose();
                //    Console.WriteLine("Disposed SpoutOutput instance and its resources.");
                //}

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
        if (_suppressFormResize || _inResize)
            return;

        RequestSwapChainResize();
    }

    private static void RequestSwapChainResize(int delayFrames = 0)
    {
        _pendingSwapChainResize = true;
        if (delayFrames > _swapChainResizeDelayFrames)
            _swapChainResizeDelayFrames = delayFrames;
    }

    internal static void ProcessPendingSwapChainResize()
    {
        if (!_pendingSwapChainResize || _inResize)
            return;

        if (_swapChain == null || _renderForm == null || _device == null || _deviceContext == null)
            return;

        if (_renderForm.ClientSize.Width == 0 || _renderForm.ClientSize.Height == 0)
            return;

        if (_swapChainResizeDelayFrames > 0)
        {
            _swapChainResizeDelayFrames--;
            return;
        }

        ResizeSwapChainToClientSize();
    }

    private static void ResizeSwapChainToClientSize()
    {
        var clientW = _renderForm!.ClientSize.Width;
        var clientH = _renderForm.ClientSize.Height;

        if (_backBuffer != null
            && _backBuffer.Description.Width == clientW
            && _backBuffer.Description.Height == clientH)
        {
            _pendingSwapChainResize = false;
            return;
        }

        _inResize = true;
        try
        {
            _deviceContext!.PixelShader.SetShaderResource(0, null);
            _deviceContext.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
            _deviceContext.Flush();

            _renderView?.Dispose();
            _renderView = null;
            _backBuffer = null;

            _swapChain!.ResizeBuffers(3, clientW, clientH, Format.Unknown, SwapChainFlags.AllowModeSwitch);

            _backBuffer = Resource.FromSwapChain<SharpDX.Direct3D11.Texture2D>(_swapChain, 0);
            _renderView = new RenderTargetView(_device, _backBuffer);
            _resolution = new Core.DataTypes.Vector.Int2(clientW, clientH);
            _resolvedOptions.Width = clientW;
            _resolvedOptions.Height = clientH;
            _pendingSwapChainResize = false;

            if (_pendingCanvasOscAfterResize)
            {
                ResetCanvasOscDedupe();
                BroadcastCanvasResolutionOsc(clientW, clientH, "buffer-resized");
                _pendingCanvasOscAfterResize = false;
            }

            Log.Debug($"Resized backbuffer to {clientW}x{clientH}");
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to resize backbuffer: {ex.Message}");
            _pendingSwapChainResize = true;
            _swapChainResizeDelayFrames = Math.Max(_swapChainResizeDelayFrames, 5);
            TryRecoverBackBufferAfterFailedResize();
        }
        finally
        {
            _inResize = false;
        }
    }

    private static void TryRecoverBackBufferAfterFailedResize()
    {
        if (_renderView != null || _swapChain == null || _device == null)
            return;

        try
        {
            _backBuffer = Resource.FromSwapChain<SharpDX.Direct3D11.Texture2D>(_swapChain, 0);
            _renderView = new RenderTargetView(_device, _backBuffer);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to recover backbuffer after resize failure: {ex.Message}");
        }
    }

    private const int CanvasOscPort = 8000;
    private static int _lastBroadcastCanvasOscW = -1;
    private static int _lastBroadcastCanvasOscH = -1;

    public static void ResetCanvasOscDedupe()
    {
        _lastBroadcastCanvasOscW = -1;
        _lastBroadcastCanvasOscH = -1;
    }

    private static bool TryLookupMonitorBounds(nint monitorHandle, out SharpDX.Rectangle bounds)
    {
        bounds = SharpDX.Rectangle.Empty;
        using var factory = new Factory1();
        for (int adapterIndex = 0; adapterIndex < factory.GetAdapterCount1(); adapterIndex++)
        {
            using var adapter = factory.GetAdapter1(adapterIndex);
            for (int outputIndex = 0; outputIndex < adapter.GetOutputCount(); outputIndex++)
            {
                using var output = adapter.GetOutput(outputIndex);
                if (output.Description.MonitorHandle != monitorHandle)
                    continue;

                var desktop = output.Description.DesktopBounds;
                bounds = new SharpDX.Rectangle(
                    desktop.Left,
                    desktop.Top,
                    desktop.Right - desktop.Left,
                    desktop.Bottom - desktop.Top);
                return true;
            }
        }

        return false;
    }

    private static void BroadcastCanvasResolutionOsc(int w, int h, string source = "player")
    {
        if (w == _lastBroadcastCanvasOscW && h == _lastBroadcastCanvasOscH)
            return;

        _lastBroadcastCanvasOscW = w;
        _lastBroadcastCanvasOscH = h;
        try
        {
            using var sender = new OscSender(IPAddress.Loopback, CanvasOscPort);
            sender.Connect();
            sender.Send(new OscMessage("/performanie/resolution.x", w.ToString()));
            sender.Send(new OscMessage("/performanie/resolution.y", h.ToString()));
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to broadcast canvas resolution OSC: {ex.Message}");
        }
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

    private static void ApplyBorderlessMonitorBounds(SharpDX.Rectangle bounds)
    {
        _renderForm!.WindowState = FormWindowState.Normal;
        _renderForm.FormBorderStyle = FormBorderStyle.None;
        _renderForm.StartPosition = FormStartPosition.Manual;
        _renderForm.MaximizeBox = false;
        _renderForm.Location = new System.Drawing.Point(bounds.Left, bounds.Top);
        _renderForm.ClientSize = new Size(bounds.Width, bounds.Height);
    }

    private static void ApplyWindowedClientSize(SharpDX.Rectangle monitorBounds, int width, int height)
    {
        _renderForm!.WindowState = FormWindowState.Normal;
        _renderForm.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        _renderForm.StartPosition = FormStartPosition.Manual;
        _renderForm.MaximizeBox = false;
        _renderForm.Location = new System.Drawing.Point(monitorBounds.X, monitorBounds.Y);
        _renderForm.ClientSize = new Size(width, height);
    }

    private static void SwitchToMonitor(nint monitorHandle, bool windowed, int width, int height, SwapChain swapChain)
    {
        bool windowed2 = windowed;
        if (_renderForm == null)
            return;

        if (!TryLookupMonitorBounds(monitorHandle, out monitorBounds))
        {
            Log.Warning($"Could not find monitor with handle {monitorHandle}.");
            return;
        }

        if (!windowed2)
        {
            width = monitorBounds.Width;
            height = monitorBounds.Height;
        }

        Log.Debug($"Switching to monitor {monitorHandle} at {monitorBounds.Left}.");

        // Wichtig: UI-Änderungen müssen im UI-Thread ausgeführt werden.
        _renderForm.Invoke(new Action(() =>
        {
            _suppressFormResize = true;
            try
            {
                _renderForm.SuspendLayout();
                if (windowed2)
                {
                    ApplyWindowedClientSize(monitorBounds, width, height);
                    if (icon != null)
                        _renderForm.Icon = icon;
                }
                else
                {
                    ApplyBorderlessMonitorBounds(monitorBounds);
                    if (icon != null)
                        _renderForm.Icon = icon;
                }
                _renderForm.ResumeLayout(true);
            }
            finally
            {
                _suppressFormResize = false;
                _pendingCanvasOscAfterResize = true;
                ResetCanvasOscDedupe();
                RequestSwapChainResize(2);
            }
        }));
    }

    public static void ApplyDisplaySettings(bool windowed, nint monitorHandle, int width, int height)
    {
        if (_renderForm == null || _swapChain == null)
            return;

        if (!windowed && TryLookupMonitorBounds(monitorHandle, out var bounds))
        {
            width = bounds.Width;
            height = bounds.Height;
        }

        _resolvedOptions.Windowed = windowed;
        _resolvedOptions.MonitorHandle = (int)monitorHandle;
        _resolvedOptions.Width = width;
        _resolvedOptions.Height = height;
        _resolution = new Core.DataTypes.Vector.Int2(width, height);

        SwitchToMonitor(monitorHandle, windowed, width, height, _swapChain);
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

    private static bool _inResize;
    private static bool _suppressFormResize;
    private static bool _pendingSwapChainResize;
    private static bool _pendingCanvasOscAfterResize;
    private static int _swapChainResizeDelayFrames;
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
    public static bool shaderCompilerbool = false;

    //Adaptergrafik
    private static string[] highPerformanceKeywords = ["dedicated", "high performance", "rtx", "gtx"];
    private static string[] integratedKeywords = ["integrated", "intel(r) uhd graphics", "microsoft basic render", "microsoft basic render"]; // twice to make MS worse
    private static nint monitorHandle;
    private static int audioDeviceIndex;
    private static string audioDevice = "Default";
    private static SharpDX.DXGI.Adapter1 selectedAdapter;
    private static Icon icon;

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
                    if (renderStarted)
                        break;
                    if (msg.Count > 0 && msg[0] is string monitorHandleStr && nint.TryParse(monitorHandleStr, out nint handle))
                    {
                        _resolvedOptions.MonitorHandle = (int)handle;
                        var w = _resolvedOptions.Width;
                        var h = _resolvedOptions.Height;
                        if (!_resolvedOptions.Windowed && TryLookupMonitorBounds(handle, out var bounds))
                        {
                            w = bounds.Width;
                            h = bounds.Height;
                        }

                        SwitchToMonitor(handle, _resolvedOptions.Windowed, w, h, _swapChain);
                    }
                    else
                    {
                        Log.Warning($"Invalid argument for /performanie/monitorhandle: {msg[0]}");
                    }
                    break;

                case "/performanie/windowed":
                    if (renderStarted)
                        break;
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
                    if (renderStarted)
                        break;
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
    
