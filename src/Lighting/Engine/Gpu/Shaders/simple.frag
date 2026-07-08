uniform float u_hueShift;   // bipolar hue rotation within the colour family
uniform float u_warmth;     // bipolar colour temperature: + warmer (amber), - cooler (blue)
// Flat solid-colour fill: one HSV swatch, no motion, gradient, rotation, or
// contrast. Base hue u_hue nudged by u_hueShift; u_saturation is the HSV
// saturation (0 = white); u_warmth pushes the colour temperature warm/cool.
// LEDs light at full brightness so a solid colour reads vivid. Every "simple*"
// key shares this shader; the colour is the per-key template tint.
void main() {
    float hue = u_hue + u_hueShift * 0.08;
    float sat = clamp(u_saturation, 0.0, 1.0);
    vec3 col = hsv2rgb(vec3(hue, sat, 1.0));
    // Colour temperature: warm lifts red / drops blue toward amber; cool does
    // the reverse toward blue, with a slight green lift so cool reads blue not cyan.
    col.r = clamp(col.r + u_warmth * 0.22, 0.0, 1.0);
    col.g = clamp(col.g + u_warmth * 0.06, 0.0, 1.0);
    col.b = clamp(col.b - u_warmth * 0.22, 0.0, 1.0);
    fragColor = vec4(col, 1.0);
}
