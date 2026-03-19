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
        finalColor = texture(uTexture, vTexCoord) * uBaseColorFactor;
    }
    else
    {
        finalColor = uBaseColorFactor;
    }
    
    if (uAlphaMode == 0)
    {
        finalColor.a = 1.0;
    }
    else if (uAlphaMode == 2)
    {
        if (finalColor.a < uAlphaCutoff)
            discard;
        finalColor.a = 1.0;
    }
    else if (uAlphaMode == 1 && uUseDitheredBlend == 1)
    {
        float alpha = clamp(finalColor.a, 0.0, 1.0);
        if (alpha < Bayer4x4Threshold(ivec2(gl_FragCoord.xy)))
            discard;
        finalColor.a = 1.0;
    }

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

uniform sampler2D uVideoTexture;
uniform sampler2D uSubtitleTexture;
uniform int uHasSubtitleTexture;
uniform int uFlipY;
uniform int uSbs3DEnabled;
uniform int uSubtitleMode; // 0 = none, 1 = stereo mono overlay

const float kSubPopoutPx = 10.0;

void main()
{
    vec2 uv = vTexCoord;
    if (uFlipY == 1)
    {
        uv.y = 1.0 - uv.y;
    }

    float eye = step(0.5, uv.x);
    float eyeX = fract(uv.x * 2.0);
    float videoX = uSbs3DEnabled == 1 ? eyeX * 0.5 + eye * 0.5 : uv.x;
    vec4 baseColor = texture(uVideoTexture, vec2(videoX, uv.y));

    if (uHasSubtitleTexture == 0 || uSubtitleMode == 0)
    {
        FragColor = vec4(baseColor.rgb, 1.0);
        return;
    }

    float subX = uv.x;
    if (uSbs3DEnabled == 1)
    {
        subX = eyeX;
        float popout = kSubPopoutPx / float(textureSize(uSubtitleTexture, 0).x);
        popout = clamp(popout, -0.01, 0.01);
        subX += (eye < 0.5 ? popout : -popout);
        subX = clamp(subX, 0.001, 0.999);
        subX = subX * 0.5 + 0.25;
    }

    vec4 subSample = texture(uSubtitleTexture, vec2(subX, uv.y));
    float alpha = clamp(subSample.a, 0.0, 1.0);
    vec3 color = mix(baseColor.rgb, subSample.rgb, alpha);
    FragColor = vec4(color, 1.0);
}
""";
    }
}
