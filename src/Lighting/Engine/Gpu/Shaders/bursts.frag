uniform float u_speed;
uniform float u_rate;      // extra: bursts per second (0.3..4)
uniform float u_particles; // extra: particles per burst (4..20)
uniform float u_size;      // extra: burst radius (0.2..1.8)

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.5;
    float rate = clamp(u_rate, 0.2, 6.0);
    int parts = int(clamp(u_particles, 3.0, 20.0));
    float sz = clamp(u_size, 0.15, 2.5);
    vec3 col = vec3(0.012, 0.008, 0.022);

    // A handful of concurrent bursts, staggered so the screen pops
    // continuously rather than in sync. Each slot maintains its own
    // burst index that advances over time.
    for (int b = 0; b < 12; b++) {
        float fb = float(b);
        float period = 1.0 / rate;
        float phaseOffset = fract(fb * 0.173) * period;
        float rawPhase = t + phaseOffset;
        float burstId = floor(rawPhase / period);
        float life = fract(rawPhase / period); // 0..1 over a single burst

        vec2 burstPos = vec2(
            hash21(vec2(burstId, fb * 17.0 + 3.0)),
            hash21(vec2(burstId, fb * 31.0 + 11.0))
        ) * 2.6 - 1.3;
        float hueIdx = hash21(vec2(burstId, fb * 53.0 + 7.0));
        vec3 tint = tintedPalette(hueIdx);
        float thisSize = sz * (0.7 + hash21(vec2(burstId, fb * 41.0)) * 0.6);

        // White-hot flash at the moment of detonation.
        float flash = smoothstep(0.18, 0.0, life) * exp(-length(uv - burstPos) * 24.0);
        col += mix(vec3(1.0), tint, 0.3) * flash * 5.2;

        // Early-out: pixels far from this burst's influence can skip the
        // entire particle loop. Trail reach is bounded by thisSize*1.2 +
        // particle exp falloff width (~0.1); 1.5 is a comfortable bound.
        if (length(uv - burstPos) > thisSize * 1.5 + 0.1) {
            continue;
        }

        // Particles streak outward on their own angles.
        for (int p = 0; p < 20; p++) {
            if (p >= parts) break;
            float fp = float(p);
            float angle = (fp + 0.5) / float(parts) * 6.28318
                        + hash21(vec2(burstId, fp * 13.0)) * 0.9;
            vec2 dir = vec2(cos(angle), sin(angle));
            float spread = 0.6 + hash21(vec2(burstId, fp * 19.0)) * 0.6;
            // Ease-out radial expansion with slight gravity droop.
            float radial = thisSize * spread * (1.0 - pow(1.0 - life, 1.8));
            vec2 ppos = burstPos + dir * radial + vec2(0.0, life * life * 0.2);
            float pd = length(uv - ppos);
            float particle = exp(-pd * 52.0);
            // Trailing line from the burst centre back to the particle.
            vec2 pd2 = uv - burstPos;
            float alongP = dot(pd2, dir);
            float perpP = length(pd2 - dir * alongP);
            float trail = smoothstep(radial, 0.0, alongP)
                        * smoothstep(0.0, 0.01, alongP)
                        * smoothstep(0.028, 0.0, perpP);
            float fade = (1.0 - life) * (1.0 - life * 0.7);
            col += tint * particle * 3.0 * fade;
            col += tint * trail * 1.4 * fade;
        }
    }
    fragColor = vec4(finalize(col), 1.0);
}
