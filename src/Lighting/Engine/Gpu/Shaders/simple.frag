uniform float u_hueShift;   // slight bipolar nudge (-1..1); scaled small so a red fill stays red
// Flat solid-colour fill: one HSV swatch, no motion, gradient, or rotation.
// Base hue u_hue is nudged slightly by u_hueShift; u_saturation is the HSV
// saturation (0 = white), u_contrast expands/compresses around mid-grey. LEDs
// light at full brightness (no tonemap) so a solid colour reads vivid. Every
// "simple*" key shares this shader; the colour is the per-key template tint.
void main() {
    float hue = u_hue + u_hueShift * 0.035;
    float sat = clamp(u_saturation, 0.0, 1.0);
    vec3 col = hsv2rgb(vec3(hue, sat, 1.0));
    col = (col - 0.5) * clamp(u_contrast, 0.0, 4.0) + 0.5;
    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
