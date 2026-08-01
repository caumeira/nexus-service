uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_offset;     // rotation of the sweep start, 0..1

// Two colours swept around the centre and blended back at the seam, so the
// wrap is smooth rather than a hard join.
void main() {
    float t = fract(sweep01() + u_offset);
    float e = 1.0 - abs(t * 2.0 - 1.0);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(mix(ca, cb, e), 1.0);
}
