#version 330 core
out vec4 fragColor;
uniform vec2  u_resolution;
uniform float u_time;
uniform float u_hue;
uniform float u_colorize;
uniform float u_saturation;
uniform float u_contrast;

// Audio-reactivity uniforms
// Populated by BeatsProvider when Music Reactive is enabled; all zero
// otherwise so any shader that reads them sees silent audio and falls
// back to its idle animation. One pipe in: pure uniforms, no parallel
// render path.
//
//   u_audioLevel : 0..1, smoothed RMS of the capture window
//   u_audioBass  : 0..1, average of spectrum bands 0..2  (~86-500 Hz)
//   u_audioMid   : 0..1, average of spectrum bands 3..8  (~500-3000 Hz)
//   u_audioHigh  : 0..1, average of spectrum bands 9..15 (~3000-22000 Hz)
//   u_audioBeat  : 1.0 on a bass-beat onset, exponential decay toward 0
//   u_spectrum   : per-band magnitudes, log-spaced, 16 entries 0..1
uniform float u_audioLevel;
uniform float u_audioBass;
uniform float u_audioMid;
uniform float u_audioHigh;
uniform float u_audioBeat;
uniform float u_spectrum[16];

// Per-effect control: 0 = effect is fully idle (audio uniforms ignored),
// 1 = full audio reactivity. Effects that don't use audio can leave it
// at 0 and nothing changes. Every existing shader sees this at 0 unless
// it's wired to sample it, so adding audio doesn't regress old visuals.
uniform float u_audioBoost;

// Presence: smooth switch that rises as soon as real audio arrives.
// Spectrum shaders use this to fade between their idle animation and the
// audio-driven form, so the frame is never static and the transition is
// smooth. Edges chosen to crest around typical-volume music so the user
// doesn't have to blast Spotify to see the effects react.
float audioPresence() {
    return smoothstep(0.03, 0.15, u_audioLevel);
}

// Derived uv (0..1, top-left origin) and aspect-corrected centered uv (-1..1)
vec2 uv01() {
    vec2 u = gl_FragCoord.xy / u_resolution;
    u.y = 1.0 - u.y;
    return u;
}
vec2 uvCentered() {
    vec2 u = uv01() * 2.0 - 1.0;
    u.x *= u_resolution.x / u_resolution.y;
    return u;
}

vec3 hsv2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

// Cosine palette - cheap, smooth, infinitely cyclable.
vec3 palette(float t, vec3 a, vec3 b, vec3 c, vec3 d) {
    return a + b * cos(6.28318 * (c * t + d));
}
vec3 rainbowPalette(float t) {
    return palette(t, vec3(0.5), vec3(0.5), vec3(1.0), vec3(0.0, 0.33, 0.67));
}
// All effects sample through this so the Hue slider shifts them uniformly.
vec3 tintedPalette(float t) {
    return rainbowPalette(t + u_hue);
}

// Soft-knee tonemap to keep HDR highlights from clipping.
vec3 tonemap(vec3 c) {
    return c / (1.0 + c);
}

// Post-process every effect's output: blend from palette-shifted colour
// (u_colorize = 0, Shifter mode) toward a grayscale-and-tint (u_colorize = 1,
// Colorizer mode) using Rec. 709 luma, then apply saturation and contrast.
// u_saturation: 0 = pure black-and-white (grayscale), 1 = unchanged, up to 4 = wildly oversaturated.
// u_contrast:   0 = flat mid-gray, 1 = unchanged, up to 4 = crushed contrast.
vec3 finalize(vec3 col) {
    float luma = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
    vec3 tint = hsv2rgb(vec3(u_hue, 1.0, 1.0));
    vec3 colorized = luma * tint * 1.4;
    vec3 blended = mix(col, colorized, clamp(u_colorize, 0.0, 1.0));
    float bl = dot(max(blended, 0.0), vec3(0.2126, 0.7152, 0.0722));
    blended = mix(vec3(bl), blended, clamp(u_saturation, 0.0, 4.0));
    blended = (blended - 0.5) * clamp(u_contrast, 0.0, 4.0) + 0.5;
    return tonemap(max(blended, 0.0));
}

// Hash + value noise - fast, cheap, good enough for lighting.
float hash21(vec2 p) {
    p = fract(p * vec2(234.34, 435.345));
    p += dot(p, p + 34.23);
    return fract(p.x * p.y);
}
float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * vnoise(p);
        p *= 2.02;
        a *= 0.5;
    }
    return v;
}
