uniform float u_speed;
uniform float u_branches; // extra: tendril count (3..12)
uniform float u_jitter;   // extra: path crackle (0.1..2)
uniform float u_power;    // extra: energy intensity (0.3..2)

// Tesla-coil style electrical tendrils branching out from a wandering
// origin. Each tendril is a polyline whose segments jitter with fbm,
// drawn as soft glowing line distance.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.5, 1000.0);
    int N = int(clamp(u_branches, 2.0, 14.0));
    float jit = clamp(u_jitter, 0.0, 3.0);
    float pwr = clamp(u_power, 0.0, 3.0);

    // Globe origin wanders slowly around the centre.
    vec2 origin = vec2(sin(t * 0.4) * 0.18, cos(t * 0.32) * 0.14);

    vec3 col = vec3(0.02, 0.01, 0.04);

    // Each tendril: a base direction + jittered path samples.
    for (int i = 0; i < 14; i++) {
        if (i >= N) break;
        float fi = float(i);
        float baseAng = (fi + 0.5) / float(N) * 6.28318 + t * 0.2;
        vec2 d = uv - origin;
        float angP = atan(d.y, d.x);
        float r = length(d);
        float angDelta = angP - baseAng;
        angDelta = mod(angDelta + 3.14159, 6.28318) - 3.14159;

        // Tendril path: along baseAng but curving with fbm along radius.
        float jitterAmp = jit * 0.28;
        float pathOffset = (fbm3(vec2(r * 4.0, fi * 3.7 + t)) - 0.5) * jitterAmp;
        // Secondary wiggle so the tendril has audible crackle shape.
        pathOffset += (fbm3(vec2(r * 12.0, fi * 5.1 + t * 2.0)) - 0.5) * 0.08;
        float perp = abs(r * angDelta - pathOffset * r);

        // Wider tendril profile + slower distance falloff so the whole
        // globe is visible, not just a wisp near the centre.
        float lineGlow = exp(-perp * 14.0) * exp(-r * 0.8);
        // Extra bright inner halo.
        lineGlow += exp(-perp * 50.0) * exp(-r * 0.5) * 0.6;

        float crackle = 0.75 + 0.45 * sin(t * 8.0 + fi * 5.7 + r * 6.0);

        vec3 tint = tintedPalette(0.58 + fi * 0.04 + r * 0.2);
        col += tint * lineGlow * crackle * pwr * 1.8;
        // White-hot core along the inner third of each tendril.
        col += vec3(1.0, 0.95, 0.95) * exp(-perp * 25.0) * exp(-r * 2.8) * 1.2;
    }

    // Bright globe core with soft halo.
    float dCore = length(uv - origin);
    col += vec3(1.0, 0.96, 1.0) * exp(-dCore * 6.0) * 0.9;
    col += tintedPalette(0.62) * exp(-dCore * 2.2) * 0.35;

    fragColor = vec4(finalize(col), 1.0);
}
