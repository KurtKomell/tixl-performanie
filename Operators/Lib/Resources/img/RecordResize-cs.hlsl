Texture2D<float4> SourceImage : register(t0);
RWTexture2D<float4> Result : register(u0);

cbuffer ResizeParams : register(b0)
{
    int SourceWidth;
    int SourceHeight;
    int TargetWidth;
    int TargetHeight;
};

float4 SampleBilinear(int2 dstPixel)
{
    float2 srcCoord = (float2(dstPixel) + 0.5) * float2(SourceWidth, SourceHeight) / float2(TargetWidth, TargetHeight) - 0.5;
    int2 baseCoord = int2(floor(srcCoord));
    float2 frac = srcCoord - float2(baseCoord);

    float4 c00 = SourceImage[clamp(baseCoord + int2(0, 0), int2(0, 0), int2(SourceWidth - 1, SourceHeight - 1))];
    float4 c10 = SourceImage[clamp(baseCoord + int2(1, 0), int2(0, 0), int2(SourceWidth - 1, SourceHeight - 1))];
    float4 c01 = SourceImage[clamp(baseCoord + int2(0, 1), int2(0, 0), int2(SourceWidth - 1, SourceHeight - 1))];
    float4 c11 = SourceImage[clamp(baseCoord + int2(1, 1), int2(0, 0), int2(SourceWidth - 1, SourceHeight - 1))];

    float4 c0 = lerp(c00, c10, frac.x);
    float4 c1 = lerp(c01, c11, frac.x);
    return lerp(c0, c1, frac.y);
}

[numthreads(16, 16, 1)]
void main(uint3 i : SV_DispatchThreadID)
{
    if (i.x >= (uint)TargetWidth || i.y >= (uint)TargetHeight)
        return;

    Result[int2(i.xy)] = SampleBilinear(int2(i.xy));
}
