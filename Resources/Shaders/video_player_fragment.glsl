#version 330 core

in vec2 vTexCoord;
out vec4 FragColor;

uniform sampler2D uVideoTexture;
uniform sampler2D uSubtitleTexture;
uniform int uHasSubtitleTexture;
uniform int uFlipY;
uniform int uSbs3DEnabled;
uniform int uSubtitleMode; // 0 = none, 1 = mono overlay duplicated per eye, 2 = stereo SBS subtitle

const float kSubPopoutPx = 25.0;

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

    vec2 subUv = uv;
    if (uSbs3DEnabled == 1 && uSubtitleMode == 1)
    {
        subUv.x = eyeX;
    }

    vec4 subSample = texture(uSubtitleTexture, subUv);
    float alpha = clamp(subSample.a, 0.0, 1.0);
    vec3 color = mix(baseColor.rgb, subSample.rgb, alpha);
    FragColor = vec4(color, 1.0);
}
