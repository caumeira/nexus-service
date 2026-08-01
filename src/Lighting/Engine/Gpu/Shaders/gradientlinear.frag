uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_angle;      // sweep direction in degrees
uniform float u_midpoint;   // where the blend sits along the sweep
uniform float u_softness;   // 0 = hard edge, 1 = full-frame blend

// Two-colour linear blend. Softness collapses the transition toward a hard
// edge, so this covers both a smooth gradient and a soft-edged split.
void main() {
    float t = axis01(u_angle);
    float w = max(u_softness, 0.001) * 0.5;
    float e = smoothstep(u_midpoint - w, u_midpoint + w, t);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
