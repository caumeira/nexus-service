uniform float u_speed;
uniform float u_rings; // extra: ring frequency along the tunnel (0.5..4)
uniform float u_zoom;  // extra: forward fly rate (0.3..3)
uniform float u_neon;  // extra: ring glow (0.3..2)

// Concentric neon rings flying toward the viewer. Depth = 1/r; rings are
// sharp bright bands in depth, coloured per ring index. Screen-space.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * clamp(u_zoom, 0.3, 3.0);
    float freq = clamp(u_rings, 0.4, 5.0);
    float neon = clamp(u_neon, 0.1, 3.0);

    float r = length(uv) + 1e-3;
    float z = (1.0 / r) * freq - t;
    float idx = floor(z);
    float band = fract(z) - 0.5;
    float ring = exp(-band * band * 22.0);

    vec3 ringCol = tintedPalette(idx * 0.08 + t * 0.03);
    vec3 col = ringCol * ring * neon * (0.4 + r * 0.8); // brighter at the near rim
    col += tintedPalette(idx * 0.08 + 0.15) * 0.06;     // dim wall fill

    col += vec3(1.0, 0.93, 0.8) * (0.55 / (1.0 + r * r * 26.0));
    fragColor = vec4(finalize(col), 1.0);
}
