uniform float u_speed;
uniform float u_blobs;    // extra: how many colour layers (3..12)
uniform float u_softness; // extra: edge blurriness (0.1..1.0)

// Voronoi-ish distance for organic blob boundaries.
float voronoi(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float d = 1.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 n = vec2(float(x), float(y));
            vec2 cell = vec2(hash21(i + n), hash21(i + n + 99.0));
            d = min(d, length(n + cell - f));
        }
    }
    return d;
}

void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.7, 1000.0);
    int layers = int(clamp(u_blobs, 2.0, 12.0));
    float soft = clamp(u_softness, 0.05, 1.5);
    vec3 col = vec3(0.0);
    float totalAlpha = 0.0;
    // Stack translucent colour blobs. Each layer is a slowly-warped
    // Voronoi field with its own palette offset and opacity.
    // Palette tints are fixed per-layer (no time dep) so changing
    // speed moves the blobs without shifting their colours.
    for (int i = 0; i < 12; i++) {
        if (i >= layers) break;
        float fi = float(i);
        float scale = 1.2 + fi * 0.35;
        float phase = fi * 0.71;
        // Domain warp: organic drift driven by speed.
        vec2 q = uv * scale + vec2(
            sin(t * 0.7 + phase) * 0.7,
            cos(t * 0.6 + phase * 1.3) * 0.6);
        q += vec2(fbm(q + t * 0.25), fbm(q + vec2(5.0, 3.0) + t * 0.2)) * 0.45;
        float v = voronoi(q);
        float mask = smoothstep(soft, 0.0, v - 0.2);
        float alpha = mask * (0.35 + 0.15 * sin(fi * 2.0));
        vec3 tint = tintedPalette(fi * 0.11);
        col = mix(col, tint, alpha);
        totalAlpha += alpha * 0.3;
    }
    // Paper background: pale warm tone darkened very slightly by paint density.
    vec3 paper = tintedPalette(0.15) * 0.12;
    col = col + paper * (1.0 - min(totalAlpha, 1.0));
    fragColor = vec4(finalize(col * 1.6), 1.0);
}
