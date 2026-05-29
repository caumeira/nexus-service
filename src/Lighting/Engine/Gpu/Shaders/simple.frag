uniform float u_speed;
// Dead-cheap near-solid colour fill. The colour itself comes entirely from
// the post-process tint (u_hue / u_colorize / u_saturation applied in
// finalize), so every "simple*" effect key shares this one shader and differs
// only by its template feels. Two low-frequency sines give a barely-there
// luminance drift so the fill gently breathes instead of being dead flat -
// two sin() per pixel, so it's about as light as a shader gets.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.06;
    float v = sin((uv.x + uv.y) * 1.4 + t)
            + sin(uv.y * 1.1 - t * 0.7);
    float luma = 0.72 + 0.045 * v;   // ~0.63 .. 0.81
    fragColor = vec4(finalize(vec3(luma)), 1.0);
}
