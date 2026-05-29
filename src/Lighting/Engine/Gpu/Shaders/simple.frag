uniform float u_speed;
uniform float u_gradient;   // 0 = flat solid colour, 1 = strong dark->light ramp
uniform float u_rotation;   // gradient angle in degrees (0 = vertical)
// Simple colour fill with a vertical gradient. The colour comes from the
// post-process tint (u_hue / u_colorize / u_saturation in finalize); this
// shader just shapes the brightness: a clean linear gradient along a
// (rotatable) axis, plus a barely-there value-noise drift so the fill still
// breathes. Cheap by design - one rotate + one noise tap. Every "simple*"
// key shares it; only the tint and these two uniforms differ.
void main() {
    vec2 uv = uv01();            // 0..1, top-left origin
    vec2 c = uv - 0.5;

    // Gradient axis. 0 deg = vertical (varies top<->bottom); rotation spins it.
    float a = radians(u_rotation);
    float axis = c.x * sin(a) + c.y * cos(a);          // ~ -0.5 .. 0.5
    float grad = axis * clamp(u_gradient, 0.0, 1.0) * 1.5;

    // Subtle drift, kept gentle so the gradient reads cleanly.
    float t = u_time * u_speed * 0.25;
    vec2 p = c * 1.7 + 0.30 * vec2(sin(t * 0.7 + uv.y * 2.0), cos(t * 0.6 + uv.x * 2.0));
    float n = vnoise(p + t * 0.20) - 0.5;

    float luma = clamp(0.62 + grad + 0.08 * n, 0.0, 1.3);
    fragColor = vec4(finalize(vec3(luma)), 1.0);
}
