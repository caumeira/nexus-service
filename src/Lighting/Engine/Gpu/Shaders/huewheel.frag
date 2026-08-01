uniform float u_aHue;       // rotation of the wheel, 0..1
uniform float u_aSat;       // colour saturation
uniform float u_aVal;       // brightness

// Hue wheel swept around the centre.
void main() {
    float t = fract(sweep01() + u_aHue);
    fragColor = vec4(hsv2rgb(vec3(t, clamp(u_aSat, 0.0, 1.0), clamp(u_aVal, 0.0, 1.0))), 1.0);
}
