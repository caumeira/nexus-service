uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_angle;      // split direction in degrees
uniform float u_position;   // where the edge sits along the sweep

// Hard two-tone split, no blend at all.
void main() {
    float t = axis01(u_angle);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(t < u_position ? ca : cb, 1.0);
}
