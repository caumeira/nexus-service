uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_spacing;    // dots across the frame
uniform float u_size;       // dot radius within its cell
uniform float u_softness;   // 0 = hard dot edge, 1 = feathered

// Dot grid over a flat background.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01();
    p.x *= ar;
    vec2 cell = fract(p * max(u_spacing, 1.0)) - 0.5;
    float d = length(cell);
    float w = max(u_softness, 0.001) * 0.3;
    float e = smoothstep(u_size - w, u_size + w, d);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(cb, ca, e), 1.0);
}
