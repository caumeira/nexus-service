uniform float u_speed;
uniform float u_curtains; // extra: number of overlapping curtain bands
uniform float u_height;   // extra: how high the aurora reaches (0.2..1.0)
uniform float u_shimmer;  // extra: high-freq flicker intensity

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.4, 1000.0);
    int bands = int(clamp(u_curtains, 1.0, 8.0));
    float h = clamp(u_height, 0.1, 1.0);
    float shim = clamp(u_shimmer, 0.0, 1.0);

    vec3 sky = mix(vec3(0.0, 0.0, 0.02), vec3(0.0, 0.01, 0.06), uv.y);
    vec3 col = sky;
    for (int i = 0; i < 8; i++) {
        if (i >= bands) break;
        float fi = float(i);
        // Each curtain: a vertically-narrow band whose horizontal position
        // is warped by layered sine waves so it ripples like fabric.
        float freq1 = 2.0 + fi * 0.7;
        float freq2 = 5.0 + fi * 1.3;
        float warp = sin(uv.x * freq1 + t * (0.5 + fi * 0.15) + fi * 0.9) * 0.12
                   + sin(uv.x * freq2 - t * (0.3 + fi * 0.1) + fi * 1.7) * 0.06;
        // Curtain center sits in the upper portion of the frame.
        float cy = 0.15 + fi * 0.06 + warp;
        // Vertical falloff: bright near the center, fading down toward u_height.
        float bandWidth = (h * 0.18 + 0.02);
        float band = exp(-pow((uv.y - cy) / bandWidth, 2.0));
        // High-freq shimmer noise along the curtain.
        float flicker = 1.0 + shim * (vnoise(vec2(uv.x * 30.0 + fi * 7.0, t * 4.0 + fi)) - 0.5) * 0.8;
        vec3 tint = tintedPalette(fi * 0.14 + 0.25);
        col += tint * band * flicker * 0.55;
    }
    // Subtle star field in the background.
    float star = pow(hash21(floor(uv * vec2(160.0, 90.0))), 18.0) * 0.6;
    col += vec3(star);
    fragColor = vec4(finalize(col), 1.0);
}
