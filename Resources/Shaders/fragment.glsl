#version 330 core
out vec4 FragColor;

in vec2 vTexCoord;

uniform int uHasTexture;
uniform sampler2D uTexture;
uniform vec4 uBaseColorFactor;
uniform int uAlphaMode;  // 0=OPAQUE, 1=BLEND, 2=MASK
uniform float uAlphaCutoff;

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
    // else BLEND (1): Use alpha as-is
    
    FragColor = finalColor;
}
