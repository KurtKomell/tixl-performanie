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
    float AlphaCutOff;
    // float Debug;
    float UseFlatShading;
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

cbuffer Params : register(b5)
{
    /*{FLOAT_PARAMS}*/
}

struct psInput
{
    float2 texCoord : TEXCOORD;
    float4 pixelPosition : SV_POSITION;
    float3 worldPosition : POSITION;
    float3x3 tbnToWorld : TBASIS;
    float fog : VPOS;
};

sampler TexSampler : register(s0);
sampler LinearSampler : register(s1);
sampler ClampedSampler : register(s2);

StructuredBuffer<PbrVertex> PbrVertices : register(t0);
StructuredBuffer<int3> FaceIndices : register(t1);

Texture2D<float4> BaseColorMap : register(t2);
Texture2D<float4> EmissiveColorMap : register(t3);
Texture2D<float4> RSMOMap : register(t4);
Texture2D<float4> NormalMap : register(t5);

TextureCube<float4> PrefilteredSpecular : register(t6);
Texture2D<float4> BRDFLookup : register(t7);

psInput vsMain(uint id : SV_VertexID)
{
    psInput output;

    int faceIndex = id / 3; //  (id % verticesPerInstance) / 3;
    int faceVertexIndex = id % 3;

    PbrVertex vertex = PbrVertices[FaceIndices[faceIndex][faceVertexIndex]];

    float4 posInObject = float4(vertex.Position, 1);

    float4 posInClipSpace = mul(posInObject, ObjectToClipSpace);
    output.pixelPosition = posInClipSpace;

    float2 uv = vertex.TexCoord;
    output.texCoord = float2(uv.x, 1 - uv.y);

    // Pass tangent space basis vectors (for normal mapping).
    float3x3 TBN = float3x3(vertex.Tangent, vertex.Bitangent, vertex.Normal);
    TBN = mul(TBN, (float3x3)ObjectToWorld);

    output.tbnToWorld = float3x3(
        normalize(TBN._m00_m01_m02),
        normalize(TBN._m10_m11_m12),
        normalize(TBN._m20_m21_m22));

    output.worldPosition = mul(posInObject, ObjectToWorld).xyz;

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
/*{RESOURCES(t8)}*/

//=== Field functions ===============================================
/*{FIELD_FUNCTIONS}*/

//-------------------------------------------------------------------

//-------------------------------------------------------------------
inline float4 GetField(float4 p)
{
#ifndef USE_WORLDSPACE
    p.xyz = mul(float4(p.xyz, 1), WorldToObject).xyz;
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
//

float3 ComputeNormal(psInput pin, float3x3 tbnToWorld)
{
    float3 N;
    if (UseFlatShading > 0.5)
    {
        // Flat shading: calculate geometric normal from world position derivatives
        float3 dpdx = ddx(frag.worldPosition);
        float3 dpdy = ddy(frag.worldPosition);
        float3 geometricNormal = normalize(cross(dpdy, dpdx));

        // Apply normal map details on top of flat normal
        float4 normalMap = NormalMap.Sample(TexSampler, frag.uv);
        float3 normalDetail = normalize(2.0 * normalMap.rgb - 1.0);

        // Create TBN basis using geometric normal and derivatives
        float3 T = normalize(dpdx);
        float3 B = normalize(cross(geometricNormal, T));
        T = cross(B, geometricNormal); // Reorthogonalize
        float3x3 flatTBN = float3x3(T, B, geometricNormal);

        // Apply normal map in flat shading tangent space
        N = normalize(mul(normalDetail, flatTBN));
    }
    else
    {
        // Standard shading: use interpolated normals with normal mapping
        float4 normalMap = NormalMap.Sample(TexSampler, frag.uv);
        N = normalize(2.0 * normalMap.rgb - 1.0);
        N = normalize(mul(N, tbnToWorld));
    }
    return N;
}

float4 psMain(psInput pin) : SV_TARGET
{
    float4 roughnessMetallicOcclusion = RSMOMap.Sample(TexSampler, pin.texCoord);

    frag.Roughness = saturate(roughnessMetallicOcclusion.x + Roughness);
    frag.Metalness = saturate(roughnessMetallicOcclusion.y + Metal);
    frag.Occlusion = roughnessMetallicOcclusion.z;
    frag.albedo = BaseColorMap.Sample(TexSampler, pin.texCoord);
    frag.uv = pin.texCoord;
    frag.N = ComputeNormal(pin, pin.tbnToWorld);
    frag.fog = pin.fog;
    frag.worldPosition = pin.worldPosition;

    float4 eyePosition = mul(float4(0, 0, 0, 1), CameraToWorld);
    frag.Lo = normalize(eyePosition.xyz - frag.worldPosition);

    float4 litColor = ComputePbr();

    litColor.rgba *= GetField(float4(pin.worldPosition.xyz, 0)).rgba;
    if (AlphaCutOff > 0 && litColor.a < AlphaCutOff)
    {
        discard;
    }
    return litColor;
}
