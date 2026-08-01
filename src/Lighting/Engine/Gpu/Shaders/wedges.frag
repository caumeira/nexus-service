uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_count;      // wedge pairs around the centre
uniform float u_offset;     // rotation of the wedges

// Pie wedges alternating between two colours - sharp radial spokes.
void main() {
    float t = fract((sweep01() + u_offset) * max(floor(u_count), 1.0));
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(t < 0.5 ? ca : cb, 1.0);
}
