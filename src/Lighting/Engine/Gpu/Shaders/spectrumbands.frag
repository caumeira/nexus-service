uniform float u_count;      // number of discrete bands
uniform float u_angle;      // band direction in degrees
uniform float u_aHue;       // starting hue
uniform float u_aSat;       // colour saturation
uniform float u_aVal;       // brightness

// Spectrum quantised into flat bands - sharp edges, no blending.
void main() {
    float n = max(floor(u_count), 2.0);
    float band = floor(axis01(u_angle) * n) / n;
    fragColor = vec4(hsv2rgb(vec3(fract(band + u_aHue), clamp(u_aSat, 0.0, 1.0), clamp(u_aVal, 0.0, 1.0))), 1.0);
}
