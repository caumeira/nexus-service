uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_count;      // stripe pairs across the frame
uniform float u_angle;      // stripe direction in degrees
uniform float u_softness;   // 0 = hard bands, 1 = sine blend
uniform float u_balance;    // width split between the two colours

// Alternating bands. Softness turns the hard band edge into a blend, so one
// shader covers sharp stripes and a repeating gradient.
void main() {
    float t = fract(axis01(u_angle) * max(u_count, 0.5));
    float w = max(u_softness, 0.001) * 0.5;
    float b = clamp(u_balance, 0.05, 0.95);
    float e = smoothstep(b - w, b + w, t);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
