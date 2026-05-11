uniform float u_speed;
uniform float u_density;    // extra: vein density (0.5..3)
uniform float u_brightness; // extra: highlight intensity (0.3..2)
uniform float u_flow;       // extra: flow rate multiplier (0.2..3)

// Underwater-style caustic light: nested fbm sampled at two scales,
// thresholded into thin bright veins on a darker base. Movement comes
// from advecting the noise sample point with time so the veins drift
// like sun through pool water on tile.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * u_flow * 0.25, 1000.0);
    float dens = clamp(u_density, 0.3, 4.0);
    float bright = clamp(u_brightness, 0.0, 3.0);

    vec2 p = uv * dens * 1.6;
    // Two warped fbm samples that flow against each other.
    float n1 = fbm(p + vec2(t, t * 0.6));
    float n2 = fbm(p * 1.7 + vec2(-t * 0.7, t * 0.45) + vec2(n1 * 1.3, n1));
    // Caustic bands form where successive noise gradients align: take the
    // distance between the two samples and invert into bright thin veins.
    float caustic = abs(n1 - n2);
    float vein = pow(1.0 - smoothstep(0.0, 0.18, caustic), 4.0);

    // Two band families slightly hue-shifted so highlights cycle colour.
    vec3 base = tintedPalette(0.55 + n1 * 0.2 + t * 0.05);
    vec3 hot  = tintedPalette(0.6 + n2 * 0.2);
    vec3 col = base * 0.18 + hot * vein * bright;
    // Dim background at deep crests so highlights feel like lit water.
    col += base * 0.12 * (1.0 - vein);

    fragColor = vec4(finalize(col), 1.0);
}
