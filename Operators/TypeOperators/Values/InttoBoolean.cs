namespace Types.Values;

[Guid("b223fbda-d11e-4389-bc89-4b9346cff20d")]
public sealed class InttoBoolean : Instance<InttoBoolean>
{
    [Output(Guid = "efb1e5ff-eaca-45c5-a671-4ea53400729a")]
    public readonly Slot<bool> Result = new();

    public InttoBoolean()
    {
        Result.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        Result.Value = BoolValue.GetValue(context);
    }
        
    [Input(Guid = "6529d61e-95e0-4e05-8f5b-e38dc44c5aa0")]
    public readonly InputSlot<bool> BoolValue = new();
        
    [Input(Guid = "5c73cde8-5523-48bb-8517-0e03c42b8050")]
    public readonly InputSlot<System.Numerics.Vector4> ColorInGraph = new();
}