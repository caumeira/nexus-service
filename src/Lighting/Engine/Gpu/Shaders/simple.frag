uniform float u_speed;
// Lightweight near-solid colour fill with a subtle drifting noise - the same
// idea as plasma (domain-warped value noise) but far cheaper: a small sine
// warp plus two value-noise taps (vs plasma's many sines / fbm). The colour
// itself comes from the post-process tint (u_hue / u_colorize / u_saturation
// in finalize), so every "simple*" key shares this one shader. The noise is
// deliberately visible - it shows as soft moving clouds in the thumbnail -
// while the high luma floor keeps the fill reading as one solid colour.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.25;
    // Gentle domain warp so the cloud cells slowly swirl instead of sliding.
    vec2 p = uv * 1.7;
    p += 0.35 * vec2(sin(t * 0.7 + uv.y * 2.0), cos(t * 0.6 + uv.x * 2.0));
    float n = vnoise(p + t * 0.20) * 0.62
            + vnoise(p * 2.1 - t * 0.15) * 0.38;
    // Bright floor + visible swing: stays one colour but clearly moves.
    float luma = 0.64 + 0.26 * n;
    fragColor = vec4(finalize(vec3(luma)), 1.0);
}
