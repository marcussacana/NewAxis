#version 330 core

in vec2 vTexCoord;
out vec4 FragColor;

uniform sampler2D uTexture;
uniform sampler2D uSubTexture;
uniform int uUseSubTexture;
uniform int uFlipY;
uniform int uSbs3DEnabled;
uniform int uSbsLayout; // 0 = Half SBS, 1 = Full SBS
uniform int uHasSubText;

const float kSubPopoutPx = 25.0;

float luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

vec4 sampleSubtitle(vec2 uv)
{
    return uUseSubTexture == 1 ? texture(uSubTexture, uv) : texture(uTexture, uv);
}

vec4 sampleSubtitleAA(vec2 uv, vec2 texel)
{
    vec2 t = texel * 0.5;
    vec4 c = sampleSubtitle(uv) * 0.40;
    c += sampleSubtitle(uv + vec2( t.x, 0.0)) * 0.15;
    c += sampleSubtitle(uv + vec2(-t.x, 0.0)) * 0.15;
    c += sampleSubtitle(uv + vec2(0.0,  t.y)) * 0.15;
    c += sampleSubtitle(uv + vec2(0.0, -t.y)) * 0.15;
    return c;
}

float subtitleMask(vec3 sampleColor, float baseLum)
{
    float lum = luma(sampleColor);
    float maxc = max(sampleColor.r, max(sampleColor.g, sampleColor.b));
    float minc = min(sampleColor.r, min(sampleColor.g, sampleColor.b));
    float chroma = maxc - minc;
    float brightText = smoothstep(0.62, 0.86, lum) * (1.0 - smoothstep(0.20, 0.45, chroma));
    float contrast = smoothstep(0.08, 0.30, lum - baseLum);
    return brightText * contrast;
}

void main()
{
    vec2 uv = vTexCoord;

    if (uFlipY == 1)
    {
        uv.y = 1.0 - uv.y;
    }

    vec4 color = texture(uTexture, uv);

    if (uSbs3DEnabled == 1 && uHasSubText == 1 && uv.y > 0.76)
    {
        float eyeLocalX = uv.x < 0.5 ? uv.x * 2.0 : (uv.x - 0.5) * 2.0;
        float srcX = eyeLocalX;

        if (uSbsLayout == 1)
        {
            srcX = 0.5 + (eyeLocalX - 0.5) * 0.5;
        }

        vec2 texel = 1.0 / vec2(textureSize(uTexture, 0));
        float popout = texel.x * kSubPopoutPx;
        srcX += (uv.x < 0.5 ? popout : -popout);

        vec2 subUv = vec2(clamp(srcX, 0.0, 1.0), uv.y);
        vec4 subSample = sampleSubtitleAA(subUv, texel);
        float baseLum = luma(color.rgb);
        float fill = subtitleMask(subSample.rgb, baseLum);

        vec2 stepUvNear = texel * 2.6;
        vec2 stepUvFar = texel * 4.2;

        float ringNear = 0.0;
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvNear.x, 0.0), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvNear.x, 0.0), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0,  stepUvNear.y), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0, -stepUvNear.y), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvNear.x,  stepUvNear.y), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvNear.x,  stepUvNear.y), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvNear.x, -stepUvNear.y), texel).rgb, baseLum));
        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvNear.x, -stepUvNear.y), texel).rgb, baseLum));

        float ringFar = 0.0;
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvFar.x, 0.0), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvFar.x, 0.0), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0,  stepUvFar.y), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0, -stepUvFar.y), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvFar.x,  stepUvFar.y), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvFar.x,  stepUvFar.y), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvFar.x, -stepUvFar.y), texel).rgb, baseLum));
        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvFar.x, -stepUvFar.y), texel).rgb, baseLum));

        float ring = max(ringNear, ringFar * 1.05);

        float lowerBand = smoothstep(0.76, 0.90, uv.y);
        float aa = max(fwidth(fill) * 2.0, 0.03);
        float fillMask = smoothstep(0.20 - aa, 0.46 + aa, fill) * lowerBand;
        float outlineMask = smoothstep(0.03, 0.22, ring) * lowerBand * (1.0 - fillMask);

        color.rgb = mix(color.rgb, vec3(0.0), outlineMask * 1.0);
        color.rgb = mix(color.rgb, subSample.rgb, fillMask);
    }

    FragColor = vec4(color.rgb, 1.0);
}
