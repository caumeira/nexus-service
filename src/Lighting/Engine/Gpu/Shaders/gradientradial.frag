uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_radius;     // where the outer colour takes over
uniform float u_softness;   // 0 = hard ring, 1 = full blend

// Centre colour fading out to an edge colour.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01() - 0.5;
    p.x *= ar;
    float d = length(p) / (0.5 * sqrt(ar * ar + 1.0));
    float w = max(u_softness, 0.001) * 0.5;
    float e = smoothstep(u_radius - w, u_radius + w, d);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
