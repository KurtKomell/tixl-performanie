﻿using ImGuiNET;
using T3.Core.Operator;
using T3.Editor.Gui.InputUi.ListInputs;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.InputUi.SingleControl;

internal sealed class FloatListInputUi : ListInputValueUi<List<float>>
{
    public override IInputUi Clone()
    {
        return new FloatListInputUi
                   {
                       InputDefinition = InputDefinition,
                       Parent = Parent,
                       PosOnCanvas = PosOnCanvas,
                       Relevancy = Relevancy
                   };
    }
    
    protected override InputEditStateFlags DrawEditControl(string name, Symbol.Child.Input input, ref List<float> list, bool readOnly)
    {
        return DrawListInputControl(input, ref list);
    }
}