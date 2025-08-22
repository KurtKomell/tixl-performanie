#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"
#include "shared/point-light.hlsl"
#include "shared/pbr.hlsl"

cbuffer Transforms : register(b0)
{
    float4x4 CameraToClipSpace;
    float4x4 ClipSpaceToCamera;
    float4x4 WorldToCamera;
    float4x4 CameraToWorld;
    float4x4 WorldToClipSpace;
    float4x4 ClipSpaceToWorld;
    float4x4 ObjectToWorld;
    float4x4 WorldToObject;
    float4x4 ObjectToCamera;
    float4x4 ObjectToClipSpace;
};

cbuffer Params : register(b1)
{
    float4 Color;

    float Scale;
    float AlphaCutOff;    
    float UseWForSize;
};

cbuffer FogParams : register(b2)
{
    float4 FogColor;
    float FogDistance;
    float FogBias;
}

cbuffer PointLights : register(b3)
{
    PointLight Lights[8];
    int ActiveLightCount;
}

cbuffer PbrParams : register(b4)
{
    float4 BaseColor;
    float4 EmissiveColor;
    float Roughness;
    float Specular;
    float Metal;
}

cbuffer IntParams : register(b5)
{
    int UsePointScale;
    int ScaleFactorMode;
};

cbuffer FieldParams : register(b6)
{
    /*{FLOAT_PARAMS}*/
};




struct psInput
{
    float2 texCoord : TEXCOORD;
    float4 pixelPosition : SV_POSITION;
    float4 color : COLOR;
    float3 worldPosition : POSITION;
    float3x3 tbnToWorld : TBASIS;
    float fog : VPOS;
};

sampler WrappedSampler : register(s0);
sampler ClampedSampler : register(s1);

StructuredBuffer<PbrVertex> PbrVertices : t0;
StructuredBuffer<int3> FaceIndices : t1;
StructuredBuffer<Point> Points : t2;

Texture2D<float4> BaseColorMap : register(t3);
Texture2D<float4> EmissiveColorMap : register(t4);
Texture2D<float4> RSMOMap : register(t5);
Texture2D<float4> NormalMap : register(t6);
TextureCube<float4> PrefilteredSpecular : register(t7);
Texture2D<float4> BRDFLookup : register(t8);

psInput vsMain(uint id
               : SV_VertexID)
{
    psInput output;

    uint faceCount, meshStride;
    FaceIndices.GetDimensions(faceCount, meshStride);

    int verticesPerInstance = faceCount * 3;

    int faceIndex = (id % verticesPerInstance) / 3;
    int faceVertexIndex = id % 3;

    uint instanceCount, instanceStride;
    Points.GetDimensions(instanceCount, instanceStride);

    int instanceIndex = id / verticesPerInstance;

    PbrVertex vertex = PbrVertices[FaceIndices[faceIndex][faceVertexIndex]];
    float4 posInObject = float4(vertex.Position, 1);

    // float resizeFromW = UseWForSize ? Points[instanceIndex].W : 1;
    // float3 resizeFromStretch = UseStretch ? Points[instanceIndex].Stretch : 1;

    float sizeFactor = ScaleFactorMode == 0
                           ? 1
                       : (ScaleFactorMode == 1) ? Points[instanceIndex].FX1
                                                : Points[instanceIndex].FX2;

    float3 s = Scale * sizeFactor * (UsePointScale ? Points[instanceIndex].Scale : 1);

    posInObject.xyz *= s; //(0, resizeFromW) * Scale * resizeFromStretch;
    float4x4 orientationMatrix = transpose(qToMatrix(normalize(Points[instanceIndex].Rotation)));
    posInObject = mul(float4(posInObject.xyz, 1), orientationMatrix);

    posInObject += float4(Points[instanceIndex].Position, 0);
    output.color = Points[instanceIndex].Color;

    float4 posInClipSpace = mul(posInObject, ObjectToClipSpace);
    output.pixelPosition = posInClipSpace;

    float2 uv = vertex.TexCoord;
    output.texCoord = float2(uv.x, 1 - uv.y);

    // Pass tangent space basis vectors (for normal mapping).
    float3x3 TBN = float3x3(vertex.Tangent, vertex.Bitangent, vertex.Normal);
    TBN = mul(TBN, (float3x3)orientationMatrix);
    TBN = mul(TBN, (float3x3)ObjectToWorld);

    output.tbnToWorld = float3x3(
        normalize(TBN._m00_m01_m02),
        normalize(TBN._m10_m11_m12),
        normalize(TBN._m20_m21_m22));

    output.worldPosition = mul(posInObject, ObjectToWorld);

    // Fog
    if (FogDistance > 0)
    {
        float4 posInCamera = mul(posInObject, ObjectToCamera);
        float fog = pow(saturate(-posInCamera.z / FogDistance), FogBias);
        output.fog = fog;
    }

    return output;
}

//=== Global functions ==============================================
/*{GLOBALS}*/

//=== Additional Resources ==========================================
/*{RESOURCES(t9)}*/

//=== Field functions ===============================================
/*{FIELD_FUNCTIONS}*/

//-------------------------------------------------------------------

//-------------------------------------------------------------------
inline float4 GetField(float4 p)
{
#ifndef USE_WORLDSPACE
    //p.xyz = mul(float4(p.xyz, 1), WorldToObject).xyz;
#endif
    float4 f = 1;
    /*{FIELD_CALL}*/

    return f;
}

float GetDistance(float3 p3)
{
    return GetField(float4(p3.xyz, 0)).w;
}
//===================================================================

#include "shared/pbr-render.hlsl"

float3 ComputeNormal(psInput pin, float3x3 tbnToWorld)
{
    float3 N;

    // Standard shading: use interpolated normals with normal mapping
    float4 normalMap = NormalMap.Sample(WrappedSampler, frag.uv);
    N = normalize(2.0 * normalMap.rgb - 1.0);
    N = normalize(mul(N, tbnToWorld));
    return N;
}

float4 psMain(psInput pin) : SV_TARGET
{
    float4 roughnessMetallicOcclusion = RSMOMap.Sample(WrappedSampler, pin.texCoord);

    frag.Roughness = saturate(roughnessMetallicOcclusion.x + Roughness);
    frag.Metalness = saturate(roughnessMetallicOcclusion.y + Metal);
    frag.Occlusion = roughnessMetallicOcclusion.z;
    frag.albedo = BaseColorMap.Sample(WrappedSampler, pin.texCoord) * pin.color;
    frag.uv = pin.texCoord;
    frag.N = ComputeNormal(pin, pin.tbnToWorld);
    frag.fog = pin.fog;
    frag.worldPosition = pin.worldPosition;

    float4 eyePosition = mul(float4(0, 0, 0, 1), CameraToWorld);
    frag.Lo = normalize(eyePosition.xyz - frag.worldPosition);

    float4 litColor = ComputePbr();

    litColor.rgba *= GetField(float4(pin.worldPosition.xyz, 0));

    if (AlphaCutOff > 0 && litColor.a < AlphaCutOff)
    {
        discard;
    }
    return litColor;

}
