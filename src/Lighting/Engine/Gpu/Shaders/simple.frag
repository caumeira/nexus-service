uniform float u_hueShift;   // slight bipolar nudge; scaled small so a red fill stays red
// Flat solid-colour fill: one HSV swatch, no motion, gradient, rotation, or
// contrast. Base hue u_hue is nudged slightly by u_hueShift; u_saturation is
// the HSV saturation (0 = white). LEDs light at full brightness so a solid
// colour reads vivid. Every "simple*" key shares this shader; the colour is
// the per-key template tint.
void main() {
    float hue = u_hue + u_hueShift * 0.035;
    float sat = clamp(u_saturation, 0.0, 1.0);
    fragColor = vec4(hsv2rgb(vec3(hue, sat, 1.0)), 1.0);
}
