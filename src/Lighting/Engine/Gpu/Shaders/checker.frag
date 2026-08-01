uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_size;       // cells across the frame

// Two-colour checkerboard.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = uv01();
    p.x *= ar;
    vec2 cell = floor(p * max(u_size, 1.0));
    float odd = mod(cell.x + cell.y, 2.0);
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    fragColor = vec4(odd < 0.5 ? ca : cb, 1.0);
}
