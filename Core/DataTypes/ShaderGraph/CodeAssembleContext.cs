﻿#nullable enable
using System.Collections.Generic;
using System.Text;
using T3.Core.Logging;

namespace T3.Core.DataTypes.ShaderGraph;

/**
 * Is passed along while collecting all connected nodes in a shader graph.
 */
public sealed class CodeAssembleContext
{
    /**
     * Resource types that need to be defined before resources...
     */
    public readonly Dictionary<string, string> ResourceTypes = new();
    
    /**
     * A dictionary containing the pure methods that can be reuses by
     * one or more graph nodes.
     */
    public readonly Dictionary<string, string> Globals = new();

    /**
     * A string builder for collecting of instances specific methods containing
     * references to unique node parameters or resources.
     */
    public readonly StringBuilder Definitions = new();

    /**
     * A string builder for collecting the actual distance function. This is the
     * primary target CollectEmbeddedShaderCode is writing to.
     * Scopes are separated by introducing new local variables for positions and field results.
     */
    public StringBuilder Calls = new();

    public void PushContext(int subContextIndex, string fieldSuffix = "")
    {
        var contextId = ContextIdStack[^1];
        var subContextId = subContextIndex + fieldSuffix;

        ContextIdStack.Add(subContextId);

        AppendCall($"float4 p{subContextId} = p{contextId};");
        AppendCall($"float4 f{subContextId} = f{contextId};");
    }

    public void PopContext()
    {
        ContextIdStack.RemoveAt(ContextIdStack.Count - 1);
    }

    public void AppendCall(string code)
    {
        Calls.Append(new string('\t', (IndentCount + 1)));
        Calls.AppendLine(code);
    }

    public void PushMethod(string name)
    {
        _subMethodCalls.Push(Calls); // keep previous stacks.
        _subMethodCallsLenght = _subMethodCalls.Count;
        
        Calls = new StringBuilder();
    }

    public string PopMethod()
    {
        if (_subMethodCalls.Count == 0 || _subMethodCalls.Count != _subMethodCallsLenght)
        {
            Log.Warning("Inconsistent shader graph nesting");
            return string.Empty;
        }

        var completedCalls = Calls;
        Calls = _subMethodCalls.Pop();
        return completedCalls.ToString();
    }

    public void Indent()
    {
        IndentCount++;
    }

    public void Unindent()
    {
        IndentCount--;
    }

    private readonly Stack<StringBuilder> _subMethodCalls = new();
    private int _subMethodCallsLenght;


    //public Stack<ShaderGraphNode> NodeStack = [];
    public readonly List<string> ContextIdStack = [];
    internal int IndentCount;
    internal int SubContextCount;

    public void Reset()
    {
        Globals.Clear();
        Definitions.Clear();
        Calls.Clear();
        _subMethodCalls.Clear();
        ContextIdStack.Clear();

        IndentCount = 0;
        SubContextCount = 0;
    }

    public override string ToString()
    {
        return ContextIdStack.Count == 0 ? "" : ContextIdStack[^1];
    }
}