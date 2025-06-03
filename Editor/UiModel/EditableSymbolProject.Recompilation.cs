﻿#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using T3.Core.Compilation;
using T3.Core.Operator;
using T3.Core.SystemUi;
using T3.Editor.Compilation;
using T3.Editor.Gui.Windows;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.Commands.Graph;
using T3.Editor.UiModel.ProjectHandling;
using GraphUtils = T3.Editor.UiModel.Helpers.GraphUtils;

namespace T3.Editor.UiModel;

/// <summary>
/// And editor functionality that handles the c# compilation of symbol classes.
/// </summary>
internal partial class EditableSymbolProject
{
    public static event Action? CompilationComplete;

    protected override ReleaseInfo ReleaseInfo
    {
        get
        {
            if(AssemblyInformation.TryGetReleaseInfo(out var releaseInfo))
                return releaseInfo;

            throw new Exception($"No release info found for project {CsProjectFile.Name}");
        }
    }

    public bool TryCompile(string sourceCode, 
                           string newSymbolName, 
                           Guid newSymbolId, 
                           string nameSpace, 
                           [NotNullWhen(true)] out Symbol? newSymbol, 
                           [NotNullWhen(true)] out SymbolUi? newSymbolUi)
    {
        var path = SymbolPathHandler.GetCorrectPath(newSymbolName, nameSpace, Folder, CsProjectFile.RootNamespace!, SourceCodeExtension);
            
        bool alreadyExists;

        _csFileWatcher.EnableRaisingEvents = false;
        MarkAsSaving();
        try
        {
            alreadyExists = File.Exists(path);
            File.WriteAllText(path, sourceCode);
            UnmarkAsSaving();
        }
        catch
        {
            Log.Error($"Could not write source code to {path}");
            newSymbol = null;
            newSymbolUi = null;
            UnmarkAsSaving();
            return false;
        }

        // non-breaking change - increment build number
        CsProjectFile.IncrementBuildNumber(1);
        
        if (TryRecompile(true))
        {
            newSymbolUi = null;
            var gotSymbol = SymbolDict.TryGetValue(newSymbolId, out newSymbol) 
                            && SymbolUiDict.TryGetValue(newSymbolId, out newSymbolUi);
            if(gotSymbol)
            {
                newSymbolUi!.FlagAsModified();
            }

            return gotSymbol;
        }
        
        // we failed compilation, so we revert the build number
        CsProjectFile.IncrementBuildNumber(-1);
        
        if (!alreadyExists)
        {
            // delete the newly created file
            try
            {
                File.Delete(path);
            }
            catch
            {
                Log.Error($"Could not delete source code at {path}");
            }
        }

        newSymbol = null;
        newSymbolUi = null;
        return false;
    }

    private bool TryRecompileWithNewSource(Symbol symbol, string newSource, [NotNullWhen(false)] out string? reason)
    {
        var id = symbol.Id;
        var gotCurrentSource = FilePathHandlers.TryGetValue(id, out var currentSourcePath);
        if (!gotCurrentSource || currentSourcePath!.SourceCodePath == null)
        {
            reason = $"Could not find original source code for symbol \"{symbol.Name}\"";
            return false;
        }

        string currentSourceCode;

        try
        {
            currentSourceCode = File.ReadAllText(currentSourcePath.SourceCodePath);
        }
        catch
        {
            reason = $"Could not read original source code at \"{currentSourcePath}\"";
            return false;
        }

        _pendingSource[id] = newSource;

        var symbolUi = SymbolUiDict[id];
        symbolUi.FlagAsModified();
            
        CsProjectFile.UpdateVersionForIOChange(1);

        if (TryRecompile(true))
        {
            reason = null;
            return true;
        }
            
        CsProjectFile.UpdateVersionForIOChange(-1);

        _pendingSource[id] = currentSourceCode;
        symbolUi.FlagAsModified();
        SaveModifiedSymbols();

        reason = "Failed to compile";
        return false;
    }

    /// <summary>
    /// this currently is primarily used when re-ordering symbol inputs and outputs
    /// </summary>
    private static bool UpdateSymbolWithNewSource(Symbol symbol, string newSource, [NotNullWhen(false)] out string? reason)
    {
        if (symbol.SymbolPackage.IsReadOnly)
        {
            reason = $"Could not update symbol '{symbol.Name}' because it is not modifiable.";
            return false;
        }

        var editableSymbolPackage = (EditableSymbolProject)symbol.SymbolPackage;
        return editableSymbolPackage.TryRecompileWithNewSource(symbol, newSource, out reason);
    }

    public static bool RenameNameSpaces(NamespaceTreeNode node, EditableSymbolProject sourcePackage, EditableSymbolProject targetPackage, string newNamespace, out string reason)
    {
        var sourceNamespace = node.GetAsString();

        sourcePackage.RenameNamespace(sourceNamespace, newNamespace, targetPackage);
        reason = string.Empty;
        return true;
    }
    
    private void RenameNamespace(string sourceNamespace, string newNamespace, EditableSymbolProject newDestinationProject)
    {
        // copy since we are modifying the collection while iterating
        var mySymbols = SymbolDict.Values.ToArray();
        foreach (var symbol in mySymbols)
        {
            if (!symbol.Namespace.StartsWith(sourceNamespace))
                continue;

            var substitutedNamespace = Regex.Replace(symbol.Namespace, sourceNamespace, newNamespace);

            ChangeNamespaceOf(symbol, substitutedNamespace, newDestinationProject, sourceNamespace);
        }
    }

    public static bool ChangeSymbolNamespace(Symbol symbol, string newNamespace, out string reason)
    {
        if (symbol.SymbolPackage is not EditableSymbolProject)
        {
            reason = $"Source project {symbol.SymbolPackage} is not editable";
            return false;
        }

        if (!TryGetEditableProjectOfNamespace(newNamespace, out var targetProject))
        {
            reason = $"Could not find project for namespace {newNamespace}";
            return false;
        }
            
        var command = new ChangeSymbolNamespaceCommand(symbol, targetProject, newNamespace, ChangeNamespace);
        UndoRedoStack.AddAndExecute(command);
        reason = string.Empty;
        return true;

        static string ChangeNamespace(Guid symbolId, string nameSpace, EditableSymbolProject sourceProject, EditableSymbolProject targetProject)
        {
            if (!SymbolUiRegistry.TryGetSymbolUi(symbolId, out var symbolUi))
            {
                return $"Could not find symbol with id {symbolId} in registry";
            }
                
            var symbol = symbolUi.Symbol;
            var currentNamespace = symbol.Namespace;
            var reason = string.Empty;
            if (currentNamespace == nameSpace)
                return reason;

            sourceProject.ChangeNamespaceOf(symbol, nameSpace, targetProject);
            return reason;
        }
    }

    private bool TryRecompile(bool updatePackage)
    {
        SaveModifiedSymbols();
        MarkAsSaving();
        AssemblyInformation.Unload();
        while (UnloadInProgress)
        {
            // wait for unload to finish
            System.Threading.Thread.Sleep(10);
        }
        
        bool success = false;
        if (CsProjectFile.TryRecompile(false))
        {
            AssemblyInformation.ChangeAssemblyDirectory(CsProjectFile.GetBuildTargetDirectory());
            success = true;
        }
        
        UnmarkAsSaving();
        _lastRecompilationTimeUtc = DateTime.UtcNow;

        if (updatePackage)
        {
            ProjectSetup.UpdateSymbolPackage(this);
        }

        return success;
    }

    internal static bool TryGetEditableProjectOfNamespace(string targetNamespace, 
                                                          [NotNullWhen(true)]  out EditableSymbolProject? targetProject)
    {
        var namespaceInfos = AllProjects
           .Select(package => new PackageNamespaceInfo(package, 
                                                       package.CsProjectFile.RootNamespace
                                                       ));

        foreach (var (editableSymbolProject, projectNamespace) in namespaceInfos)
        {
            if (projectNamespace == null)
                continue;

            if (targetNamespace.StartsWith(projectNamespace))
            {
                targetProject = editableSymbolProject;
                return true;
            }
        }

        targetProject = null;
        return false;
    }

    private bool ChangeNamespaceOf(Symbol symbol, string newNamespace, EditableSymbolProject newDestinationProject, string? sourceNamespace = null)
    {
        var id = symbol.Id;
        if (HasHome && ReleaseInfo.HomeGuid == id)
        {
            Log.Error($"Cannot change namespace of home symbol {symbol}");
            return false;
        }
            
        sourceNamespace ??= symbol.Namespace;
            
        string newSourceCode, originalCode;
        if (FilePathHandlers.TryGetValue(id, out var filePathHandler) && filePathHandler.SourceCodePath != null)
        {
            if (!TryConvertToValidCodeNamespace(sourceNamespace, out var sourceCodeNamespace))
            {
                Log.Error($"Source namespace {sourceNamespace} is not a valid namespace. This is a bug.");
                return false;
            }

            if (!TryConvertToValidCodeNamespace(newNamespace, out var newCodeNamespace))
            {
                Log.Error($"{newNamespace} is not a valid namespace.");
                return false;
            }

            originalCode = File.ReadAllText(filePathHandler.SourceCodePath);
            newSourceCode = Regex.Replace(originalCode, sourceCodeNamespace, newCodeNamespace);
        }
        else
        {
            Log.Error($"Could not find source code for {symbol.Name} in {CsProjectFile.Name} ({id})");
            return false;
        }

            
        newDestinationProject._pendingSource[id] = newSourceCode;

        var symbolUi = SymbolUiDict[id];
        symbolUi.FlagAsModified();

        if (newDestinationProject != this)
        {
            GiveSymbolToPackage(id, newDestinationProject);
        }

        bool success = true;
        if(!newDestinationProject.TryRecompile(false) || !(newDestinationProject != this && !TryRecompile(false)))
        {
            // revert 
            if(newDestinationProject != this)
            {
                newDestinationProject.GiveSymbolToPackage(id, this);
                newDestinationProject.SaveModifiedSymbols();
            }
            
            _pendingSource[id] = originalCode;
            SaveModifiedSymbols();
            success = false;
        }
        
        ProjectSetup.UpdateSymbolPackages(this, newDestinationProject);
        return success;
    }

    public bool TryGetPendingSourceCode(Guid symbolId, out string? sourceCode)
    {
        return _pendingSource.TryGetValue(symbolId, out sourceCode);
    }

    private static bool TryConvertToValidCodeNamespace(string sourceNamespace, out string result)
    {
        // prepend any reserved words with a '@'
        var parts = sourceNamespace.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!GraphUtils.IsIdentifierValid(part))
            {
                var newPart = "@" + part;
                if (!GraphUtils.IsIdentifierValid(newPart))
                {
                    result = string.Empty;
                    return false;
                }

                parts[i] = newPart;
            }
        }

        result = string.Join('.', parts);
        return true;
    }

    private readonly record struct PackageNamespaceInfo(EditableSymbolProject Project, string? RootNamespace);

    private readonly CodeFileWatcher _csFileWatcher;

    public static bool RecompileSymbol(Symbol symbol, string newSource, bool flagDependentOpsAsModified, out string? reason)
    {
        if (!UpdateSymbolWithNewSource(symbol, newSource, out reason))
        {
            Log.Error(reason);
            var title = $"Could not update symbol '{symbol.Name}'";
            BlockingWindow.Instance.ShowMessageBox(reason, title);
            reason = title + ": " + reason;
            return false;
        }

        if (flagDependentOpsAsModified)
            FlagDependentOpsAsModified(symbol);
        return true;

        static void FlagDependentOpsAsModified(Symbol symbol)
        {
            List<SymbolUi> readOnlyDependents = [];
            int countModified = 0;
            foreach (var dependent in Structure.CollectDependingSymbols(symbol))
            {
                var package = (EditorSymbolPackage)dependent.SymbolPackage;
                if (!package.TryGetSymbolUi(dependent.Id, out var symbolUi))
                {
                    Log.Error($"Could not find symbol UI for [{dependent.Name}] ({dependent.Id})");
                    continue;
                }
                    
                if (!package.IsReadOnly)
                {
                    symbolUi.FlagAsModified();
                    countModified++;
                }
                else
                {
                    readOnlyDependents.Add(symbolUi);
                }
            }
            
            Log.Debug($"Modified {countModified} dependent symbols and {readOnlyDependents.Count} read-only dependent symbols.");

            if (readOnlyDependents.Count > 0)
            {
                var packages = readOnlyDependents.Select(x => x.Symbol.SymbolPackage).Distinct();
                foreach (var package in packages)
                {
                    Log.Warning($"Read-only symbol package {package.DisplayName} had a dependency modified. [{symbol.Id}]: {symbol.Name}");
                }

                foreach (var symbolUi in readOnlyDependents)
                {
                    symbolUi.UpdateConsistencyWithSymbol();
                }
            }
        }
    }
}