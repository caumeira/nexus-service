uniform float u_speed;
uniform float u_density;   // extra: cloud thickness (0.3..2.0)
uniform float u_stars;     // extra: star brightness (0..1)
uniform float u_depth;     // extra: number of cloud layers (2..6)

void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 1.6, 1000.0);
    float dens = max(0.1, u_density * 0.5);
    float starBr = clamp(u_stars, 0.0, 1.5);
    int layers = int(clamp(u_depth, 1.0, 6.0));

    // Deep space background.
    vec3 col = vec3(0.005, 0.003, 0.015);

    // Star field (before nebula so stars peek through thin regions).
    vec2 starGrid = floor(uv * 80.0);
    float starVal = hash21(starGrid);
    float starMask = step(0.97, starVal);
    col += vec3(pow(starVal, 40.0)) * starBr * 0.9;

    // Layered gas clouds. Each layer has its own colour, scale, and drift.
    for (int i = 0; i < 6; i++) {
        if (i >= layers) break;
        float fi = float(i);
        float scale = 1.5 + fi * 0.7;
        vec2 q = uv * scale + vec2(
            sin(t * 0.3 + fi * 1.4) * 0.3,
            cos(t * 0.25 + fi * 2.1) * 0.2);
        // Domain warp for organic cloud shapes.
        q += vec2(fbm3(q + t * 0.08), fbm3(q + vec2(3.0, 7.0) + t * 0.06)) * 0.5;
        float n = fbm(q);
        // High-freq detail.
        n = n * 0.7 + fbm3(q * 3.0 - t * 0.04) * 0.3;
        float cloud = smoothstep(0.3, 0.7, n * dens);
        // Each layer gets its own palette slice for colour variety.
        vec3 tint = tintedPalette(fi * 0.17 + 0.05);
        // Additive: brighter where clouds overlap.
        col += tint * cloud * (0.25 + 0.1 * fi);
    }
    // Subtle emission glow around the densest regions.
    float glow = fbm(uv * 2.0 + t * 0.05);
    col += tintedPalette(0.4) * smoothstep(0.55, 0.8, glow * dens) * 0.2;
    fragColor = vec4(finalize(col), 1.0);
}
