uniform float u_angle;      // ramp direction in degrees
uniform float u_density;    // hue cycles across the frame
uniform float u_aHue;       // starting hue
uniform float u_aSat;       // colour saturation
uniform float u_aVal;       // brightness

// Full-spectrum ramp held still: the rainbow, as a fixed painted strip.
void main() {
    float t = axis01(u_angle) * max(u_density, 0.05) + u_aHue;
    fragColor = vec4(hsv2rgb(vec3(fract(t), clamp(u_aSat, 0.0, 1.0), clamp(u_aVal, 0.0, 1.0))), 1.0);
}
