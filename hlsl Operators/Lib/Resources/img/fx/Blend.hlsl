cbuffer ParamConstants : register(b0)
{
    float4 ImageAColor;
    float4 ImageBColor;
    float ColorMode;
    float AlphaMode;
    float UseNormalForUpperHalf;
    float ScaleMode;
}


struct vsOutput
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD;
};

Texture2D<float4> ImageA : register(t0);
Texture2D<float4> ImageB : register(t1);
sampler texSampler : register(s0);

// Conversion matrices for RGB to YUV and vice versa
static const float3x3 RgbToYuv = {
    {0.299, 0.587, 0.114},
    {-0.14713, -0.28886, 0.436},
    {0.615, -0.51499, -0.10001}
};

static const float3x3 YuvToRgb = {
    {1.0, 0.0, 1.13983},
    {1.0, -0.39465, -0.58060},
    {1.0, 2.03211, 0.0}
};

float3 rgb2yuv(float3 rgb) 
{
    return mul(rgb, RgbToYuv);
}

float3 yuv2rgb(float3 yuv) 
{
    return mul(yuv, YuvToRgb);
}

float3 RgbToHsl(float3 color)
{
    float maxVal = max(color.r, max(color.g, color.b));
    float minVal = min(color.r, min(color.g, color.b));
    float h = 0.0, s = 0.0, l = (maxVal + minVal) / 2.0;

    if (maxVal == minVal)
    {
        h = s = 0.0; // achromatic
    }
    else
    {
        float d = maxVal - minVal;
        s = l > 0.5 ? d / (2.0 - maxVal - minVal) : d / (maxVal + minVal);
        if (maxVal == color.r)
        {
            h = (color.g - color.b) / d + (color.g < color.b ? 6.0 : 0.0);
        }
        else if (maxVal == color.g)
        {
            h = (color.b - color.r) / d + 2.0;
        }
        else
        {
            h = (color.r - color.g) / d + 4.0;
        }
        h /= 6.0;
    }
    return float3(h, s, l);
}

float HueToRgb(float p, float q, float t)
{
    if (t < 0.0) t += 1.0;
    if (t > 1.0) t -= 1.0;
    if (t < 1.0/6.0) return p + (q - p) * 6.0 * t;
    if (t < 1.0/2.0) return q;
    if (t < 2.0/3.0) return p + (q - p) * (2.0/3.0 - t) * 6.0;
    return p;
}

float3 HslToRgb(float3 hsl)
{
    float r, g, b;

    if (hsl.y == 0.0)
    {
        r = g = b = hsl.z; // achromatic
    }
    else
    {
        float q = hsl.z < 0.5 ? hsl.z * (1.0 + hsl.y) : hsl.z + hsl.y - hsl.z * hsl.y;
        float p = 2.0 * hsl.z - q;
        r = HueToRgb(p, q, hsl.x + 1.0/3.0);
        g = HueToRgb(p, q, hsl.x);
        b = HueToRgb(p, q, hsl.x - 1.0/3.0);
    }
    return float3(r, g, b);
}

float3 HardLight(float3 a, float3 b)
{
    return (b < 0.5) ? (2.0 * a * b) : (1.0 - 2.0 * (1.0 - a) * (1.0 - b));
}

float3 SoftLight(float3 a, float3 b)
{
    return (b < 0.5) ? (2.0 * a * b + a * a * (1.0 - 2.0 * b)) : (sqrt(a) * (2.0 * b - 1.0) + 2.0 * a * (1.0 - b));
}

float IsBetween(float value, float low, float high)
{
    return (value >= low && value <= high) ? 1 : 0;
}

float4 psMain(vsOutput psInput) : SV_TARGET
{
    float2 uv = psInput.texCoord;

    int height, width;

    ImageA.GetDimensions(width, height);
    float imageAAspect = (float)width / height;

    ImageB.GetDimensions(width, height);
    float imageBAspect = (float)width / height;


    float aspectDifference = (imageAAspect - imageBAspect) * (ScaleMode > 1.5 ? 1 : -1);

    float2 uvB = ScaleMode < 0.5 ? uv : (aspectDifference < 0
    ? float2(
        (uv.x - 0.5) * imageAAspect / imageBAspect + 0.5,
        uv.y)
    : float2(
        uv.x,
        (uv.y - 0.5) * imageBAspect / imageAAspect + 0.5));


    float4 tA = ImageA.Sample(texSampler, uv) * ImageAColor;
    float4 tB = ImageB.Sample(texSampler, uvB) * ImageBColor;
    tA.a = clamp(tA.a, 0, 1);
    tB.a = clamp(tB.a, 0, 1);

    float a = tA.a + tB.a - tA.a * tB.a;

    switch ((int)AlphaMode)
    {

        // case 1:
        //     a = tA.a;
        //     break;

    case 1:
        a = tA.a * tB.a;
        break;

    case 2:
        a = 1;
        break;

    case 3:
        a = tA.a;
        break;

    case 4:
        a = tB.a;
        break;

    case 5:
        a = (tA.r + tA.g + tA.b) / 3;
        break;

    case 6:
        a = (tB.r + tB.g + tB.b) / 3;
        break;

    case 7:
        a = tA.a + tB.a;
        break;

    case 8:
        a = max(tA.a, tB.a);
        break;
    }

    float normalRatio = saturate(tB.a * 2 - 1);

    if (UseNormalForUpperHalf > 0.5)
        tB.a = saturate(tB.a * 2);

    // float3 rgb = (1.0 - tB.a)*tA.rgb + tB.a*tB.rgb;
    float3 rgbNormalBlended = (1.0 - tB.a) * tA.rgb + tB.a * tB.rgb;
    float3 rgb = 1;

    switch ((int)ColorMode)
    {
        // normal
    case 0:
        rgb = rgbNormalBlended;
        break;

        // screen
    case 1:
        // rgb = 1 - saturate(1 - tA.rgb) * saturate(1 - tB.rgb * tB.a);
        rgb = tA.rgb + tB.rgb * tB.a;
        break;

        // multiply
    case 2:
        rgb = lerp(tA.rgb, tA.rgb * tB.rgb, tB.a);
        break;

        // overlay
    case 3:
        rgb = float3(
            tA.r < 0.5 ? (2.0 * tA.r * tB.r) : (1.0 - 2.0 * (1.0 - tA.r) * (1.0 - tB.r)),
            tA.g < 0.5 ? (2.0 * tA.g * tB.g) : (1.0 - 2.0 * (1.0 - tA.g) * (1.0 - tB.g)),
            tA.b < 0.5 ? (2.0 * tA.b * tB.b) : (1.0 - 2.0 * (1.0 - tA.b) * (1.0 - tB.b)));

        rgb = lerp(tA.rgb, rgb, tB.a);
        break;

        // difference
    case 4:
        rgb = abs(tA.rgb - tB.rgb) * tB.a + tB.rgb * (1.0 - tB.a);
        break;

        // use a
    case 5:
        rgb = tA.rgb;
        break;

        // use b
    case 6:
        rgb = tB.rgb;
        break;
        // max
    case 7:
        rgb = max(tA.rgb, tB.rgb);
        break;
        //sub
    case 8:
        rgb = tA.rgb - tB.rgb;
        break;
        //MixUsingImageBA
    case 9:
        rgb = lerp(tA.rgb, tB.rgb, ImageBColor.a);
        a = lerp(tA.a, tB.a, ImageBColor.a);
        break;
        // Additive
    case 10:
        rgb = lerp(tA.rgb, tA.rgb + tB.rgb, tB.a);
        break;
        // Atop
    case 11:
        rgb = tB.rgb * tA.a + tA.rgb * (1 - tB.a);
        break;
        // Color Burn
    case 12:
        rgb = lerp(tA.rgb, 1.0 - (1.0 - tA.rgb) / (tB.rgb + 0.00001), tB.a);
        break;
        // Chroma Difference
    case 13:
    {
        float3 yuvA = rgb2yuv(tA.rgb);
        float3 yuvB = rgb2yuv(tB.rgb);
        float3 yuvResult = float3(yuvA.x, abs(yuvA.y - yuvB.y), abs(yuvA.z - yuvB.z));
        rgb = yuv2rgb(yuvResult);
        rgb = lerp(tA.rgb, rgb, tB.a);
    }
    break;
    // Exclude
    case 14:
        rgb = lerp(tA.rgb, tA.rgb + tB.rgb - 2.0 * tA.rgb * tB.rgb, tB.a);
        break;
        // Glow
    case 15:
        rgb = lerp(tA.rgb, tA.rgb * tA.rgb / (1.0 - tB.rgb + 0.00001), tB.a);
        break;
        // Hard Light
    case 16:
        rgb = lerp(tA.rgb, HardLight(tA.rgb, tB.rgb), tB.a);
        break;
        // Hard Mix
    case 17:
        rgb = lerp(tA.rgb, step(1.0, tA.rgb + tB.rgb), tB.a);
        break;
        // Heat
    case 18:
        rgb = lerp(tA.rgb, 1.0 - (1.0 - tA.rgb) / (tB.rgb + 0.00001), tB.a);
        break;
        // Hue
    case 19:
    {
        float3 hslA = RgbToHsl(tA.rgb);
        float3 hslB = RgbToHsl(tB.rgb);
        rgb = HslToRgb(float3(hslB.x, hslA.y, hslA.z));
        rgb = lerp(tA.rgb, rgb, tB.a);
    }
    break;
    // Inverse
    case 20:
        rgb = lerp(tA.rgb, abs(1.0 - tA.rgb - tB.rgb), tB.a);
        break;
        // Lighter Color
    case 21:
        rgb = lerp(tA.rgb, max(tA.rgb, tB.rgb), tB.a);
        break;
        // Luminance Difference
    case 22:
    {
        float lumA = dot(tA.rgb, float3(0.299, 0.587, 0.114));
        float lumB = dot(tB.rgb, float3(0.299, 0.587, 0.114));
        rgb = tA.rgb + (lumB - lumA);
        rgb = lerp(tA.rgb, rgb, tB.a);
    }
    break;
    // Negate
    case 23:
        rgb = lerp(tA.rgb, 1.0 - abs(1.0 - tA.rgb - tB.rgb), tB.a);
        break;
        // Outside Luminance
    case 24:
    {
        float lumA = dot(tA.rgb, float3(0.299, 0.587, 0.114));
        float lumB = dot(tB.rgb, float3(0.299, 0.587, 0.114));
        rgb = lerp(tA.rgb, tB.rgb, step(lumA, 0.5) * (1.0 - step(lumB, 0.5)) + (1.0 - step(lumA, 0.5)) * step(lumB, 0.5));
    }
    break;
    // Over
    case 25:
        rgb = lerp(tA.rgb, tB.rgb, tB.a);
        a = tA.a + tB.a * (1.0 - tA.a);
        break;
        // Pin Light
    case 26:
    {
        float3 result;
        result.r = (tB.r > 0.5) ? max(tA.r, 2.0 * (tB.r - 0.5)) : min(tA.r, 2.0 * tB.r);
        result.g = (tB.g > 0.5) ? max(tA.g, 2.0 * (tB.g - 0.5)) : min(tA.g, 2.0 * tB.g);
        result.b = (tB.b > 0.5) ? max(tA.b, 2.0 * (tB.b - 0.5)) : min(tA.b, 2.0 * tB.b);
        rgb = lerp(tA.rgb, result, tB.a);
    }
    break;
    // Reflect
    case 27:
        rgb = lerp(tA.rgb, tB.rgb * tB.rgb / (1.0 - tA.rgb + 0.00001), tB.a);
        break;
        // Soft Light
    case 28:
        rgb = lerp(tA.rgb, SoftLight(tA.rgb, tB.rgb), tB.a);
        break;
        // Linear Light
    case 29:
        rgb = lerp(tA.rgb, (tB.rgb < 0.5) ? (tA.rgb + 2.0 * tB.rgb - 1.0) : (tA.rgb + 2.0 * (tB.rgb - 0.5)), tB.a);
        break;
        // Stencil Luminance
    case 30:
    {
        float lumB = dot(tB.rgb, float3(0.299, 0.587, 0.114));
        rgb = tA.rgb;
        a = tA.a * lumB;
    }
    break;
    // Vivid Light
    case 31:
    {
        float3 result;
        result.r = (tB.r < 0.5) ? 1.0 - (1.0 - tA.r) / (2.0 * tB.r + 0.00001) : tA.r / (2.0 * (1.0 - tB.r) + 0.00001);
        result.g = (tB.g < 0.5) ? 1.0 - (1.0 - tA.g) / (2.0 * tB.g + 0.00001) : tA.g / (2.0 * (1.0 - tB.g) + 0.00001);
        result.b = (tB.b < 0.5) ? 1.0 - (1.0 - tA.b) / (2.0 * tB.b + 0.00001) : tA.b / (2.0 * (1.0 - tB.b) + 0.00001);
        rgb = lerp(tA.rgb, saturate(result), tB.a);
    }
    break;
    // Xor
    case 32:
    {
        rgb = lerp(tA.rgb, tB.rgb * (1.0 - tA.a) + tA.rgb * (1.0 - tB.a), 1.0);
        a = tA.a + tB.a - 2.0 * tA.a * tB.a;
    }
    break;
    // Y-Film
    case 33:
        rgb = lerp(tA.rgb, tA.rgb * tB.rgb * 2.0, tB.a);
        break;
        // Z-Film
    case 34:
        rgb = lerp(tA.rgb, (tA.rgb + tB.rgb) / 2.0, tB.a);
        break;
    }


    if (UseNormalForUpperHalf > 0.5)
        rgb = lerp(rgb, rgbNormalBlended, normalRatio);


    return float4(rgb, a);
}