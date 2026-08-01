uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_cHue;
uniform float u_cSat;
uniform float u_cVal;
uniform float u_angle;      // sweep direction in degrees
uniform float u_midpoint;   // position of the middle colour along the sweep

// Three-stop blend: A to B to C across the frame.
void main() {
    float t = axis01(u_angle);
    float m = clamp(u_midpoint, 0.02, 0.98);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    vec3 cc = hsv2rgb(vec3(u_cHue, u_cSat, u_cVal));
    vec3 col = t < m
        ? mix(ca, cb, t / m)
        : mix(cb, cc, (t - m) / (1.0 - m));
    fragColor = vec4(col, 1.0);
}
