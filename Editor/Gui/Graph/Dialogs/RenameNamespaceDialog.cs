﻿#nullable enable
using ImGuiNET;
using T3.Core.SystemUi;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows;
using T3.Editor.UiModel;

namespace T3.Editor.Gui.Graph.Dialogs;

internal sealed class RenameNamespaceDialog : ModalDialog
{
    internal void Draw(NamespaceTreeNode subtreeNodeToRename)
    {
        if (BeginDialog("Move or rename namespace"))
        {
            var dialogJustOpened = _node == null;
            var nodeName = _node == null ? "undefined" : _node.GetAsString();
            
            if (dialogJustOpened)
            {
                _node = subtreeNodeToRename;
                _nameSpace = nodeName;

                EditableSymbolProject.TryGetEditableProjectOfNamespace(_nameSpace, out _projectToCopyFrom);
                _projectToCopyTo = _projectToCopyFrom;
            }

            var hasProjectToCopyFrom = _projectToCopyFrom != null;
            if (hasProjectToCopyFrom)
            {
                SymbolModificationInputs.DrawProjectDropdown(ref _nameSpace, ref _projectToCopyTo);

                if (_projectToCopyTo != null)
                {
                    DrawRenameFields();
                    ImGui.SameLine();
                }
            }
            else
            {
                ImGui.TextColored(UiColors.StatusError, $"No source project found for namespace {nodeName}");
            }
                
            if (ImGui.Button("Cancel"))
            {
                Close();
            }

            EndDialogContent();
        }

        EndDialog();
    }

    private static void DrawRenameFields()
    {
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        if (_projectToCopyTo == null || _node == null || _projectToCopyFrom == null)
        {
            ImGui.Text("invalid project data");
            return;
        }
        
        _ = SymbolModificationInputs.DrawNamespaceInput(ref _nameSpace, _projectToCopyTo, false, out var namespaceValid);

        CustomComponents.HelpText("Careful now. This operator might affect a lot of operator definitions");

        if (CustomComponents.DisablableButton("Rename", namespaceValid))
        {
            if (EditableSymbolProject.RenameNameSpaces(_node, _projectToCopyFrom, _projectToCopyTo, _nameSpace, out var reason))
            {
                T3Ui.Save(false);
            }
            else
            {
                BlockingWindow.Instance.ShowMessageBox(reason, "Could not rename namespace");
            }

            Close();
        }
    }

    private static void Close()
    {
        ImGui.CloseCurrentPopup();
        _node = null;
        _projectToCopyTo = null;
        _projectToCopyFrom = null;
    }

    private static NamespaceTreeNode? _node;
    private static string _nameSpace= string.Empty;
    private static EditableSymbolProject? _projectToCopyFrom;
    private static EditableSymbolProject? _projectToCopyTo;
}