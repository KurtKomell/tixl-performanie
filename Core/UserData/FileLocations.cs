using System;
using System.IO;
using T3.Core.Compilation;

namespace T3.Core.UserData;

/// <summary>
/// A collection of critical files and directors.
/// All classes in core and Editor should use these if possible. 
/// </summary>
public static class FileLocations
{
    public const string AppSubFolder = "TiXL";
    public const string ThemeSubFolder = "Themes";
    public const string RenderSubFolder = "RenderOutput";
    public const string ExportFolderName = "T3Exports";
    public const string KeyBindingSubFolder = "KeyBindings";
    public static string TempFolder => Path.Combine(SettingsPath, "Tmp");

    
    /// <summary>
    /// We extract this because this will later not be available for published versions.
    /// Providing this at this location will help refactoring later. 
    /// </summary>
    private static string StartFolder => RuntimeAssemblies.CoreDirectory!;
    
    /// <summary>
    /// A subfolder next in the editor start folder.
    /// </summary>
    public static string ReadOnlySettingsPath => Path.Combine(StartFolder, ".tixl");
    
    public static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                     AppSubFolder
                     
                     // Skip process name to avoid double nesting of TiXL
                     // This will lump together logs from player
                     
                     //, Process.GetCurrentProcess().ProcessName
                     );
    
    public static readonly string DefaultProjectFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppSubFolder);
    public const string ResourcesSubfolder = "Resources";
    public const string DependenciesFolder = "dependencies";
    public const string SymbolsSubfolder = "Symbols";
    public const string SymbolUiSubFolder = "SymbolUis";
    public const string SourceCodeSubFolder = "SourceCode";
}
