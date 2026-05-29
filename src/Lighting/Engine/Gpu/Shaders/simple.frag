uniform float u_speed;
uniform float u_gradient;   // 0 = flat solid colour, 1 = strong dark->light ramp
uniform float u_rotation;   // gradient angle in degrees (0 = vertical)
uniform float u_wave;       // 0 = still, 1 = pronounced gentle flowing wave
// Simple colour fill: a (rotatable) vertical gradient with a gentle wave that
// flows along the SAME direction as the gradient - a slow, broad brightness
// swell drifting up the ramp. The colour comes from the post-process tint
// (u_hue / u_colorize / u_saturation in finalize); this shader only shapes
// brightness. Every "simple*" key shares this shader.
void main() {
    vec2 uv = uv01();            // 0..1, top-left origin
    vec2 c = uv - 0.5;

    // Gradient axis. 0 deg = vertical; Y is inverted so the default ramp runs
    // bright-top -> dark-bottom. Rotation spins the axis.
    float a = radians(u_rotation);
    float axis = c.x * sin(a) - c.y * cos(a);          // ~ -0.5 .. 0.5
    float grad = axis * clamp(u_gradient, 0.0, 1.0) * 1.5;

    // Gentle wave travelling along the gradient axis: a low-frequency sine in
    // `axis` whose phase drifts with time, so soft bright/dark bands flow up
    // the gradient. Low amplitude keeps it a subtle swell, not a ripple.
    float t = u_time * u_speed * 0.25;
    float wave = sin(axis * 11.0 - t * 2.0);
    float flow = clamp(u_wave, 0.0, 1.0) * 0.25 * wave;

    float luma = clamp(0.62 + grad + flow, 0.0, 1.3);
    fragColor = vec4(finalize(vec3(luma)), 1.0);
}
