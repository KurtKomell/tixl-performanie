﻿#nullable enable
using ImGuiNET;
using T3.Core.Operator;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Modification;

namespace T3.Editor.Gui.Graph.Dialogs;

internal sealed class DuplicateSymbolDialog : ModalDialog
{
    internal DuplicateSymbolDialog()
    {
        Flags = ImGuiWindowFlags.NoScrollWithMouse;
        DialogSize = new Vector2(500, 450) * T3Ui.UiScaleFactor;
    }    
    
    public event Action? Closed;
        
    /** returns true if modified */
    public ChangeSymbol.SymbolModificationResults Draw(Instance compositionOp, List<SymbolUi.Child> selectedChildUis, ref string nameSpace, ref string newTypeName, ref string description, bool isReload = false)
    {
        //DialogSize = new Vector2(500, 450) * T3Ui.UiScaleFactor;
        var result = ChangeSymbol.SymbolModificationResults.Nothing;
        
        if(selectedChildUis.Count != 1)
            return result;

        
        if(isReload && !_completedReloadPrompt)
        {
            //DialogSize = new Vector2(400, 200);
            if (BeginDialog("Changes made to readonly operator"))
            {
                ImGui.TextWrapped("You've made changes to a read-only operator.\nDo you want to save your changes as a new operator?");
                    
                if(ImGui.Button("Yes"))
                {
                    _completedReloadPrompt = true;
                }
                    
                ImGui.SameLine();
                    
                if(ImGui.Button("No"))
                {
                    ImGui.CloseCurrentPopup();
                    Closed?.Invoke();
                }
                    
                EndDialogContent();
            }
                
            EndDialog();
            return ChangeSymbol.SymbolModificationResults.Nothing;
        }

        DialogSize = new Vector2(600, 400);

        if (BeginDialog("Duplicate as new symbol"))
        {
            _ = SymbolModificationInputs.DrawProjectDropdown(ref nameSpace, ref _projectToCopyTo);

            if (_projectToCopyTo != null)
            {
                _ = SymbolModificationInputs.DrawSymbolNameAndNamespaceInputs(ref newTypeName, ref nameSpace, _projectToCopyTo, out var symbolNamesValid);
                ImGui.Spacing();

                FormInputs.DrawInputLabel("Description");
                ImGui.InputTextMultiline("##description", ref description, 1024, new Vector2(450, 60));
                    
                if (CustomComponents.DisablableButton("Duplicate", symbolNamesValid, enableTriggerWithReturn: false))
                {
                    var compositionSymbolUi = compositionOp.GetSymbolUi();
                    var position = selectedChildUis.First().PosOnCanvas + new Vector2(0, 100);

                    Duplicate.DuplicateAsNewType(compositionSymbolUi, _projectToCopyTo, selectedChildUis.First().SymbolChild.Symbol.Id, newTypeName, nameSpace, description,
                                                 position);
                    
                    result = ChangeSymbol.SymbolModificationResults.StructureChanged;
                    T3Ui.Save(false);
                    ImGui.CloseCurrentPopup();
                    _completedReloadPrompt = false;
                    Closed?.Invoke();
                }

                ImGui.SameLine();
            }

            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
                _completedReloadPrompt = false;
                Closed?.Invoke();
            }

            EndDialogContent();
        }
        else
        {
            _completedReloadPrompt = false;
            Closed?.Invoke();
        }

        EndDialog();
        return result;
    }

    private EditableSymbolProject? _projectToCopyTo;
    private bool _completedReloadPrompt;
}