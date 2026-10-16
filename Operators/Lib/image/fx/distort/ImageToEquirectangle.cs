namespace Lib.image.fx.distort;

[Guid("b70943d7-4a2a-4bd3-b854-121dec505633")]
internal sealed class ImageToEquirectangle : Instance<ImageToEquirectangle>
{
    [Output(Guid = "bac708cc-d39c-49c6-a473-e0908434e871")]
    public readonly Slot<Texture2D> Output = new();

    [Input(Guid = "ca37206a-5048-404b-8692-2e118212e6c6")]
    public readonly InputSlot<Texture2D> Image = new();

    [Input(Guid = "1d1f9b79-fe0f-4fe8-ad8c-82406673883b")]
    public readonly InputSlot<float> PoleFade = new();

    [Input(Guid = "43373457-22d5-4d5f-b4ed-f62ba2bde760")]
    public readonly InputSlot<Vector2> Rotation = new();

    [Input(Guid = "4cec4395-bd09-4ab3-9f8b-34dafd4837c1")]
    public readonly InputSlot<float> FieldOfView = new();

    [Input(Guid = "550e4bd7-5f65-4c85-9ef9-efa3547bc2d1")]
    public readonly InputSlot<Int2> Resolution = new();

    [Input(Guid = "aa700b09-9931-4b6a-84e4-8489f811b9fe")]
    public readonly InputSlot<Vector4> Background = new();
}
