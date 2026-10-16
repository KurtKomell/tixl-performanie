using SharpDX;
using SharpDX.Direct3D11;

namespace Types.Gfx;

[Guid("9515d59d-0bd5-406b-96da-6a5f60215700")]
public sealed class SamplerState : Instance<SamplerState>
{
    [Output(Guid = "{0E45C596-C80F-4927-941F-E3199401AA10}")]
    public readonly Slot<SharpDX.Direct3D11.SamplerState> Value = new();

    public SamplerState()
    {
        Value.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        // Unconnected InputSlots default to 0. D3D11 rejects 0 for AddressMode/Comparison
        // (valid enums start at 1). Some GPUs/drivers are strict → E_INVALIDARG (0x80070057).
        var filter = Filter.GetValue(context);
        if (!Enum.IsDefined(typeof(Filter), filter))
            filter = SharpDX.Direct3D11.Filter.MinMagMipLinear;

        var addressU = SanitizeAddress(AddressU.GetValue(context));
        var addressV = SanitizeAddress(AddressV.GetValue(context));
        var addressW = SanitizeAddress(AddressW.GetValue(context));

        var mipBias = MipLoadBias.GetValue(context);

        var maxAnisotropy = MaximumAnisotropy.GetValue(context);
        // D3D11 requires MaxAnisotropy in [1, 16]. Unconnected inputs default to 0.
        if (maxAnisotropy < 1)
            maxAnisotropy = 1;
        else if (maxAnisotropy > 16)
            maxAnisotropy = 16;

        var comparison = ComparisonFunction.GetValue(context);
        if (!Enum.IsDefined(typeof(Comparison), comparison) || (int)comparison == 0)
            comparison = Comparison.Never;

        var minLod = MinimumLod.GetValue(context);
        var maxLod = MaximumLod.GetValue(context);
        // Unconnected LOD slots default to 0/0 — invalid on some GPUs.
        // Match ResourceManager.DefaultSamplerState.
        if (minLod == 0 && maxLod == 0)
        {
            minLod = -float.MaxValue;
            maxLod = float.MaxValue;
        }
        else if (maxLod < minLod)
        {
            maxLod = minLod;
        }

        var samplerDesc = new SamplerStateDescription()
                              {
                                  Filter = filter,
                                  AddressU = addressU,
                                  AddressV = addressV,
                                  AddressW = addressW,
                                  MipLodBias = mipBias,
                                  MaximumAnisotropy = maxAnisotropy,
                                  ComparisonFunction = comparison,
                                  BorderColor = new SharpDX.Mathematics.Interop.RawColor4(1.0f, 1.0f, 1.0f, 1.0f),
                                  MinimumLod = minLod,
                                  MaximumLod = maxLod
                              };

        try
        {
            var samplerState = new SharpDX.Direct3D11.SamplerState(ResourceManager.Device, samplerDesc);

            // Never Dispose the shared default — that blacks out the whole renderer.
            if (Value.Value != null && !ReferenceEquals(Value.Value, ResourceManager.DefaultSamplerState))
                Value.Value.Dispose();
            Value.Value = samplerState;
        }
        catch (SharpDXException e)
        {
            Log.Error($"{Parent?.Symbol.Name}.SamplerStateOp: Invalid sampler state " + e.Message);
            if (Value.Value == null)
            {
                Value.Value = ResourceManager.DefaultSamplerState;
                Log.Error("Using the default sampler state instead.", this);
            }
            else
            {
                Log.Error("Using the last valid sampler state instead.", this);
            }
        }
    }

    private static TextureAddressMode SanitizeAddress(TextureAddressMode mode)
    {
        // TextureAddressMode: Wrap=1 … MirrorOnce=5. 0 is invalid.
        if (!Enum.IsDefined(typeof(TextureAddressMode), mode) || (int)mode == 0)
            return TextureAddressMode.Clamp;
        return mode;
    }

    [Input(Guid = "{A870921F-A28C-4501-9F31-38A18B0ACDCC}")]
    public readonly InputSlot<Filter> Filter = new();

    [Input(Guid = "{E7C95FD5-14D1-434F-A140-F22EF69076AB}")]
    public readonly InputSlot<TextureAddressMode> AddressU = new();

    [Input(Guid = "{FDEB503F-09C6-48D1-8853-7426F68CDEC3}")]
    public readonly InputSlot<TextureAddressMode> AddressV = new();

    [Input(Guid = "{93D8BF26-5067-4CCC-B4CB-E03970686462}")]
    public readonly InputSlot<TextureAddressMode> AddressW = new();

    [Input(Guid = "{4B51422E-1DA7-4A28-B55F-47881D42F801}")]
    public readonly InputSlot<float> MipLoadBias = new();

    [Input(Guid = "{1CCE7427-2062-42FD-838E-328DEF3ECB30}")]
    public readonly InputSlot<int> MaximumAnisotropy = new();

    [Input(Guid = "{393A3E40-5C58-48C7-84D2-C4E8D03F7373}")]
    public readonly InputSlot<Comparison> ComparisonFunction = new();

    //        [Input(Guid = "{5A6E8282-EBF6-4641-9574-A29E04F08B2E}")]
    //        public InputSlot<RawColor4> BorderColor = new InputSlot<RawColor4>();

    [Input(Guid = "{05531EF5-72AA-4868-915F-A40D26DA9E80}")]
    public readonly InputSlot<float> MinimumLod = new();

    [Input(Guid = "{7D5346DB-DF9C-4BCE-9F39-065983253A7F}")]
    public readonly InputSlot<float> MaximumLod = new();
}
