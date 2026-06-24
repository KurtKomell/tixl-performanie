// Bicubic upscale with mild sharpening (NVIDIA Image Scaling style)
Texture2D<float4> SourceImage : register(t0);
RWTexture2D<float4> Result : register(u0);

cbuffer UpscaleParams : register(b0)
{
    int SourceWidth;
    int SourceHeight;
    int TargetWidth;
    int TargetHeight;
};

static const float sharpen = 0.15;

float CubicWeight(float x)
{
    x = abs(x);
    if (x <= 1.0)
        return 1.5 * x * x * x - 2.5 * x * x + 1.0;
    if (x < 2.0)
        return -0.5 * x * x * x + 2.5 * x * x - 4.0 * x + 2.0;
    return 0.0;
}

float4 SampleBicubic(int2 dstPixel)
{
    float2 srcCoord = (float2(dstPixel) + 0.5) * float2(SourceWidth, SourceHeight) / float2(TargetWidth, TargetHeight) - 0.5;
    int2 baseCoord = int2(floor(srcCoord));
    float2 frac = srcCoord - float2(baseCoord);

    float4 color = float4(0, 0, 0, 0);
    float weightSum = 0.0;

    [unroll]
    for (int j = -1; j <= 2; j++)
    {
        [unroll]
        for (int i = -1; i <= 2; i++)
        {
            int2 sampleCoord = clamp(baseCoord + int2(i, j), int2(0, 0), int2(SourceWidth - 1, SourceHeight - 1));
            float w = CubicWeight(float(i) - frac.x) * CubicWeight(float(j) - frac.y);
            color += SourceImage[sampleCoord] * w;
            weightSum += w;
        }
    }

    return color / max(weightSum, 1e-5);
}

[numthreads(16, 16, 1)]
void main(uint3 i : SV_DispatchThreadID)
{
    if (i.x >= (uint)TargetWidth || i.y >= (uint)TargetHeight)
        return;

    float4 center = SampleBicubic(int2(i.xy));
    float4 neighbors = (
        SampleBicubic(int2(i.xy) + int2(1, 0)) +
        SampleBicubic(int2(i.xy) + int2(-1, 0)) +
        SampleBicubic(int2(i.xy) + int2(0, 1)) +
        SampleBicubic(int2(i.xy) + int2(0, -1))
    ) * 0.25;

    float4 sharpened = center + (center - neighbors) * sharpen;
    Result[int2(i.xy)] = saturate(sharpened);
}
