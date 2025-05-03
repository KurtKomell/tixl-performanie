﻿#nullable enable

using System.Diagnostics.CodeAnalysis;
using T3.Core.Operator;
using T3.Editor.UiModel;

namespace T3.Editor.Gui.Windows;

/// <summary>
/// A nested container that can contain further instances of <see cref="NamespaceTreeNode"/>
/// Used to structure the <see cref="SymbolLibrary"/>.
/// </summary>
internal sealed class NamespaceTreeNode
{
    internal string Name { get; private set; }
    internal List<NamespaceTreeNode> Children { get; } = new();
    private NamespaceTreeNode? Parent { get; }

    internal NamespaceTreeNode(string name, NamespaceTreeNode? parent = null)
    {
        Name = name;
        Parent = parent;
    }

    internal string GetAsString()
    {
        var list = new List<string>();
        var t = this;
        while (t.Parent != null)
        {
            list.Insert(0, t.Name);
            t = t.Parent;
        }

        return string.Join(".", list);
    }

    private void Clear()
    {
        Children.Clear();
        Symbols.Clear();
    }

    internal void PopulateCompleteTree()
    {
        Name = RootNodeId;
        Clear();

        foreach (var symbol in EditorSymbolPackage.AllSymbols.OrderBy(symbol => symbol.Namespace + symbol.Name))
        {
            SortInOperator(symbol);
        }
    }
    // define an action delegate that takes a Symbol and returns a bool

    internal void PopulateCompleteTree(Predicate<SymbolUi> filterAction)
    {
        Name = RootNodeId;
        Clear();

        foreach (var ui in EditorSymbolPackage.AllSymbolUis.OrderBy(ui => ui.Symbol.Namespace + ui.Symbol.Name))
        {
            if(filterAction(ui))
                SortInOperator(ui.Symbol);
        }
    }

    private void SortInOperator(Symbol symbol)
    {
        if (symbol.Namespace == null)
        {
            return;
        }

        var spaces = symbol.Namespace.Split('.');

        var currentNode = this;
        var expandingSubTree = false;

        foreach (var spaceName in spaces)
        {
            if (spaceName == "")
                continue;

            if (!expandingSubTree)
            {
                if(currentNode.TryFindNodeDataByName(spaceName, out var node))
                {
                    currentNode = node;
                }
                else
                {
                    expandingSubTree = true;
                }
            }

            if (!expandingSubTree)
                continue;

            var newNode = new NamespaceTreeNode(spaceName, currentNode);
            currentNode.Children.Add(newNode);
            currentNode = newNode;
        }

        currentNode.Symbols.Add(symbol);
    }

    private bool TryFindNodeDataByName(string name, [NotNullWhen(true)]out  NamespaceTreeNode? node)
    {
        node=Children.FirstOrDefault(n => n.Name == name);
        return node != null;
    }

    internal readonly List<Symbol> Symbols = [];
    internal const string RootNodeId = "root";
}