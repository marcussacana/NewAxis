namespace NewAxis.Graphics
{
    internal static class ShaderSources
    {
        public const string MeshVertex = """
#version 330 core
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec2 aTexCoord;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec2 vTexCoord;

void main()
{
    vTexCoord = aTexCoord;
    gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
}
""";

        public const string MeshFragment = """
#version 330 core
out vec4 FragColor;

in vec2 vTexCoord;

uniform int uHasTexture;
uniform sampler2D uTexture;
uniform vec4 uBaseColorFactor;
uniform int uAlphaMode;  // 0=OPAQUE, 1=BLEND, 2=MASK
uniform float uAlphaCutoff;
uniform int uUseDitheredBlend;

float Bayer4x4Threshold(ivec2 pixel)
{
    int x = pixel.x & 3;
    int y = pixel.y & 3;
    int idx = y * 4 + x;
    const float matrix[16] = float[](
        0.0,  8.0,  2.0, 10.0,
        12.0, 4.0, 14.0,  6.0,
        3.0, 11.0,  1.0,  9.0,
        15.0, 7.0, 13.0,  5.0
    );
    return (matrix[idx] + 0.5) / 16.0;
}

void main()
{
    vec4 finalColor;
    
    if (uHasTexture == 1)
    {
        // Sample texture and multiply by base color factor
        finalColor = texture(uTexture, vTexCoord) * uBaseColorFactor;
    }
    else
    {
        // No texture, use base color factor directly
        finalColor = uBaseColorFactor;
    }
    
    // Apply alpha mode
    if (uAlphaMode == 0)
    {
        // OPAQUE: Force alpha to 1.0
        finalColor.a = 1.0;
    }
    else if (uAlphaMode == 2)
    {
        // MASK: Discard if below cutoff
        if (finalColor.a < uAlphaCutoff)
            discard;
        finalColor.a = 1.0;  // Fully opaque if not discarded
    }
    else if (uAlphaMode == 1 && uUseDitheredBlend == 1)
    {
        // DITHERED BLEND: convert alpha to screen-door coverage
        float alpha = clamp(finalColor.a, 0.0, 1.0);
        if (alpha < Bayer4x4Threshold(ivec2(gl_FragCoord.xy)))
            discard;
        finalColor.a = 1.0;
    }
    // else BLEND (1): use alpha as-is

    FragColor = finalColor;
}
""";

        public const string VideoPlayerVertex = """
#version 330 core
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;

out vec2 vTexCoord;

void main()
{
    gl_Position = vec4(aPosition, 1.0);
    vTexCoord = aTexCoord;
}
""";

        public const string VideoPlayerFragment = """
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
uniform float uAspectZoomY;
uniform int uStereoToMono;

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

    if (uAspectZoomY > 1.001)
    {
        uv.y = ((uv.y - 0.5) / uAspectZoomY) + 0.5;
        uv.y = clamp(uv.y, 0.0, 1.0);
    }

    if (uStereoToMono == 1)
    {
        uv.x *= 0.5;
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
""";
    }
}
