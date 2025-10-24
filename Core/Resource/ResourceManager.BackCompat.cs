#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using T3.Core.Logging;
using T3.Core.Model;
using T3.Core.Utils;

namespace T3.Core.Resource;

public static partial class ResourceManager
{
    private static bool RelativePathBackwardsCompatibility(string relativePath, 
                                                           out bool isAbsolute, 
                                                           [NotNullWhen(true)] out List<Range>? backCompatPaths)
    {
        backCompatPaths = null;
        if (Path.IsPathRooted(relativePath))
        {
            
            // var isProbablyFromFilePathScanning = relativePath.IndexOf("Editor/bin/Debug/net9.0-windows/Resources", StringComparison.Ordinal) != -1;
            // if(!isProbablyFromFilePathScanning)
            //     Log.Warning($"Path '{relativePath}' is not relative. This is deprecated and should be relative to the project Resources folder as " +
            //                 $"live updates will not occur. Please update your project settings.");
            
            isAbsolute = true;
            return false;
        }

        isAbsolute = false;

        var pathSpan = relativePath.AsSpan();

        var subfolderRangeArray = _rangeArrayPool.Get();
        var wholeSubfolderSpan = subfolderRangeArray.AsSpan();
        var subfolderCount = StringUtils.SplitByDirectory(pathSpan, wholeSubfolderSpan);

        if (subfolderCount <= 0)
        {
            throw new Exception("This should not happen, even if there are no subfolders.");
        }

        var subfolderRanges = subfolderRangeArray.AsSpan(0, subfolderCount);

        List<Range>? backCompatRanges = [];

        // test for shaders that reference using lib/etc
        int index = 0;
        if (AddNextRangeIfMatch("lib", pathSpan, subfolderRanges))
        {
            backCompatPaths = backCompatRanges;
            return true;
        }
        
        // remove "resources" prefix
        if (AddNextRangeIfMatch("resources", pathSpan, subfolderRanges))
        {
            index++;
        }
        else // if there is no "resources" prefix, we do not need to apply any back-compat rules
        {
            return false;
        }

        // test again for "Resources/lib" references
        if (AddNextRangeIfMatch("lib", pathSpan, subfolderRanges))
            index++;

        // "t3/Resources/common" has been flattened into "t3/Resources"
        if(AddNextRangeIfMatch("common", pathSpan, subfolderRanges))
            index++;

        if (AddNextRangeIfMatch("user", pathSpan, subfolderRanges))
        {
            // "user" subfolders expected an additional subfolder after them
            // e.g. "t3/Resources/user/pixtur/shaders"
            // so we should remove this as it is not needed and may be incorrect
            backCompatRanges!.RemoveAt(backCompatRanges.Count - 1);

            // remove username subfolder
            index++;
            AddNextRangeIfMatch("*", pathSpan, subfolderRanges);
        }
        
        backCompatPaths = backCompatRanges;
        ReturnPooledArray();
        return true;

        void ReturnPooledArray() => _rangeArrayPool.Return(subfolderRangeArray, false);

        static bool TryCreateRange(int startIndex, int endIndex, ReadOnlySpan<char> path,out Range range)
        {
            if (startIndex < endIndex && endIndex <= path.Length)
            {
                range = new Range(startIndex, endIndex);
                return true;
            }

            range = default;
            return false;
        }

        // TODO: this is too hard to read and should be static instead.
        bool AddNextRangeIfMatch(string testString, ReadOnlySpan<char> pathSpan, Span<Range> subfolderRanges)
        {
            if(index >= subfolderRanges.Length)
                return false;
            
            var range = subfolderRanges[index];
            var subfolder = pathSpan[range];
            var nextStartIndex = range.End.Value + 1;
            var testStringSpan = testString.AsSpan();
        
            if (testStringSpan.Length == 0 && testStringSpan[0] == '*' || StringUtils.Equals(subfolder, testStringSpan, true))
            {
                if (TryCreateRange(nextStartIndex, pathSpan.Length, pathSpan, out var newRange))
                {
                    backCompatRanges ??= new List<Range>();
                    backCompatRanges.Add(newRange);
                    return true;
                }
            }

            return false;
        }
    }

    private static IReadOnlyList<string> PopulateBackCompatPaths(string original, List<Range>? backCompatRanges)
    {
        if (backCompatRanges is null or { Count: 0 })
            return Array.Empty<string>();

        var list = new List<string>(backCompatRanges!.Count);
        foreach (var range in backCompatRanges)
        {
            list.Add(original[range]);
        }

        return list;
    }

    // ReSharper disable once FieldCanBeMadeReadOnly.Local
    private static Utils.ObjectPooling.ArrayPool<Range> _rangeArrayPool = new(true, 30);

    internal static bool TryConvertToRelativePath(string newPath, [NotNullWhen(true)] out string? relativePath)
    {
        foreach (var package in SymbolPackage.AllPackages)
        {
            var folder = package.ResourcesFolder;
            if (newPath.StartsWith(folder))
            {
                relativePath = '/' + $"{package.Alias}/{newPath[folder.Length..]}";
                relativePath.ToForwardSlashesUnsafe();
                return true;
            }
        }

        relativePath = null;
        return false;
    }
    private static bool _disposed = false;

    public static void Dispose()
    {
        if (_disposed)
            return;

        // Dispose the ArrayPool
        if (_rangeArrayPool != null)
        {
            // Optional: Clear any remaining objects in the pool
            _rangeArrayPool = null;
        }

        // Log the disposal for debugging purposes
        Log.Info("ResourceManager has been disposed.");

        _disposed = true;
    }

}