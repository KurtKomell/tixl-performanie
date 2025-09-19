namespace Lib.image.use;

[Guid("46daab0e-e957-413e-826c-0699569d0e07")]
internal sealed class CustomPixelShader : Instance<CustomPixelShader>
{

    [Input(Guid = "8c3ffefe-8721-4dde-b252-22eb8be02d3f")]
    public readonly InputSlot<string> ShaderCode = new();

    [Input(Guid = "674cabbd-cf31-46ac-9a1a-4f6bd727c977")]
    public readonly InputSlot<Vector2> Center = new();

    [Input(Guid = "3d84725a-594b-46d8-aa21-eec99026115d")]
    public readonly InputSlot<float> A = new();

    [Input(Guid = "b4895a95-5ff4-4583-9ec3-befcf0f7b18b")]
    public readonly InputSlot<float> B = new();

    [Input(Guid = "60bdd684-8005-4576-b09b-1b5d6124da1d")]
    public readonly InputSlot<float> C = new();

    [Input(Guid = "db522fd4-5cfc-49f6-9983-02ec0dd6090a")]
    public readonly InputSlot<float> D = new();

    [Input(Guid = "83e06d04-02bd-40cc-8666-d5dd62a9e63e")]
    public readonly InputSlot<Int2> Resolution = new();

    [Input(Guid = "5f90f885-0ccc-4014-a921-dc710257835a")]
    public readonly InputSlot<Texture2D> FxTexture = new();

    [Input(Guid = "fb8d51fe-b4c2-452a-9e53-b649aed92bd7")]
    public readonly InputSlot<bool> IgnoreTemplate = new();

        [Input(Guid = "c9a801ec-13fb-4ad4-b0cd-d125b5db500a")]
        public readonly InputSlot<string> AdditionalCode = new InputSlot<string>();

    [Output(Guid = "12fcfd9e-1c2f-46fc-b570-83b93ec7d101")]
    public readonly Slot<Texture2D> TextureOutput = new();
}