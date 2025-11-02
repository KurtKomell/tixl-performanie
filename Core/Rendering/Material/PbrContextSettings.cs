using System.Diagnostics.CodeAnalysis;
using SharpDX.Direct3D11;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Resource;
using Texture2D = T3.Core.DataTypes.Texture2D;

namespace T3.Core.Rendering.Material;

public static class PbrContextSettings
{
    static PbrContextSettings()
    {
        WhitePixelTexture = TextureUtils.CreateColorTexture(new Vector4(1, 1, 1, 1));
        BlackPixelTexture = TextureUtils.CreateColorTexture(new Vector4(0, 0, 0, 0));

        _bdrfLookupTextureResource = ResourceManager.CreateTextureResource("pbr/BRDF-LookUp.dds", null);
        if (!TryLoadTextureAsSrv("pbr/BRDF-LookUp.dds", out _bdrfLookupTextureResource, ref PbrLookUpTextureSrv))
        {
            Log.Error("Could not load prefiltered BRDF texture");
            _bdrfLookupTextureResource.Changed += () =>
            {
                PbrLookUpTextureSrv = new ShaderResourceView(ResourceManager.Device, _bdrfLookupTextureResource.Value);
            };
        }
        
        _prefilteredBrdfTextureResource = ResourceManager.CreateTextureResource("images/hdri/studio_small_08-prefiltered.dds", null);
        if (_prefilteredBrdfTextureResource.Value == null)
        {
            Log.Error("Could not load prefiltered BRDF texture");
        }

        _defaultMaterial = PbrMaterial.CreateDefault();
    }

    private static bool TryLoadTextureAsSrv(string imagePath, out Resource<Texture2D> textureResource, [NotNullWhen(false)] ref ShaderResourceView srv)
    {
        textureResource = ResourceManager.CreateTextureResource(imagePath, null);
        var texture = textureResource.Value;
        if (texture == null) return false;
        
        texture.CreateShaderResourceView(ref srv, imagePath);
        return true;
    }

    public static void SetDefaultToContext(EvaluationContext context)
    { 
        context.Materials.Clear();
        context.PbrMaterial = _defaultMaterial;
        context.ContextTextures[PrefilteredSpecularId] = _prefilteredBrdfTextureResource.Value;
    }

    private static readonly PbrMaterial _defaultMaterial;

    public static readonly Texture2D WhitePixelTexture; // Beispiel: Weiße Textur mit Alpha 1; // TODO: move to something like shared resource
    public static readonly Texture2D BlackPixelTexture; // Beispiel: Schwarze Textur mit Alpha 1; // TODO: move to something like shared resource

    public static ShaderResourceView PbrLookUpTextureSrv;
    
    public const string PrefilteredSpecularId = "PrefilteredSpecular";
    private static readonly Resource<Texture2D> _bdrfLookupTextureResource;
    private static readonly Resource<Texture2D> _prefilteredBrdfTextureResource;

}