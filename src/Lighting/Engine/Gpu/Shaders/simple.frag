uniform float u_speed;
uniform float u_gradient;   // 0 = flat solid colour, 1 = strong dark->light ramp
uniform float u_rotation;   // gradient angle in degrees (0 = vertical)
uniform float u_noise;      // 0 = clean, 1 = strong soft cloudy wash
// Simple colour fill: a (rotatable) vertical gradient plus a soft, blurry,
// slowly drifting noise wash. The colour comes from the post-process tint
// (u_hue / u_colorize / u_saturation in finalize); this shader only shapes
// brightness. The noise is built from two LOW-frequency smooth (value) noise
// octaves, so it reads as gentle moving clouds that blend in - never gritty
// per-pixel grain. Every "simple*" key shares this shader.
void main() {
    vec2 uv = uv01();            // 0..1, top-left origin
    vec2 c = uv - 0.5;

    // Gradient axis. 0 deg = vertical; Y is inverted so the default ramp runs
    // bright-top -> dark-bottom. Rotation spins the axis.
    float a = radians(u_rotation);
    float axis = c.x * sin(a) - c.y * cos(a);          // ~ -0.5 .. 0.5
    float grad = axis * clamp(u_gradient, 0.0, 1.0) * 1.5;

    // Soft, blurry, drifting cloud noise. Low base frequency + a gentle domain
    // warp keep it large-scale and pleasant; two octaves give it body without
    // turning gritty (vnoise is smoothstep-interpolated, so no hard grain).
    float t = u_time * u_speed * 0.25;
    vec2 p = c * 1.9 + 0.25 * vec2(sin(t * 0.6 + c.y * 3.0), cos(t * 0.5 + c.x * 3.0));
    float n = vnoise(p + t * 0.15) * 0.62
            + vnoise(p * 2.2 - t * 0.10 + 13.0) * 0.38;     // ~0 .. 1, centred ~0.5
    float noise = clamp(u_noise, 0.0, 1.0) * 0.8 * (n - 0.5);

    float luma = clamp(0.62 + grad + noise, 0.0, 1.3);
    fragColor = vec4(finalize(vec3(luma)), 1.0);
}
