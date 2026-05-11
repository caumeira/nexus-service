uniform float u_speed;
uniform float u_folds;  // extra: fold count (2..12)
uniform float u_flow;   // extra: drift speed (0..2)
uniform float u_sheen;  // extra: specular highlight (0..2)

void main() {
    vec2 uv = uvCentered();
    // Base speed multiplier raised so the cloth actually flows at default
    // speed=50 - the old value (0.35) read as near-static until the user
    // pushed the slider hard.
    float t = mod(u_time * u_speed * 0.9, 1000.0);
    float folds = clamp(u_folds, 1.0, 14.0);
    float flow = clamp(u_flow, 0.0, 2.5);
    float sheen = clamp(u_sheen, 0.0, 2.5);

    // Cloth "height field" is a sum of sinusoidal folds travelling at
    // different angles + a domain-warped noise layer for organic ripples.
    // Normal-like direction is estimated via derivatives of the height field
    // so specular highlights track the folds.
    vec2 p = uv;
    float tw = t * flow;

    float h = 0.0;
    for (int i = 0; i < 14; i++) {
        float fi = float(i);
        if (fi >= folds) break;
        float ang = fi * 0.7 + sin(t * 0.15 + fi) * 0.3;
        vec2 d = vec2(cos(ang), sin(ang));
        float freq = 1.5 + fi * 0.6;
        float phase = fi * 0.9 + tw * (0.4 + fi * 0.08);
        h += sin(dot(p, d) * freq + phase) * (1.0 / (1.0 + fi * 0.6));
    }
    // Warped noise for cloth texture.
    h += (fbm(p * 2.0 + tw * 0.1) - 0.5) * 0.5;

    // Finite-diff normal (cheap).
    float eps = 0.003;
    float hx = h;
    // recompute at +eps, -eps for x -- compact inline:
    float hxp = 0.0, hxm = 0.0, hyp = 0.0, hym = 0.0;
    for (int k = 0; k < 4; k++) {
        vec2 shift;
        if (k == 0) shift = vec2(eps, 0.0);
        else if (k == 1) shift = vec2(-eps, 0.0);
        else if (k == 2) shift = vec2(0.0, eps);
        else shift = vec2(0.0, -eps);
        float sum = 0.0;
        vec2 pp = p + shift;
        for (int i = 0; i < 14; i++) {
            float fi = float(i);
            if (fi >= folds) break;
            float ang = fi * 0.7 + sin(t * 0.15 + fi) * 0.3;
            vec2 d = vec2(cos(ang), sin(ang));
            float freq = 1.5 + fi * 0.6;
            float phase = fi * 0.9 + tw * (0.4 + fi * 0.08);
            sum += sin(dot(pp, d) * freq + phase) * (1.0 / (1.0 + fi * 0.6));
        }
        sum += (fbm(pp * 2.0 + tw * 0.1) - 0.5) * 0.5;
        if (k == 0) hxp = sum;
        else if (k == 1) hxm = sum;
        else if (k == 2) hyp = sum;
        else hym = sum;
    }
    vec2 grad = vec2(hxp - hxm, hyp - hym) / (2.0 * eps);
    vec3 n = normalize(vec3(-grad, 1.0));

    // Light direction rotates slowly so the silk catches the light.
    vec3 L = normalize(vec3(sin(t * 0.3) * 0.7, cos(t * 0.25) * 0.7, 0.6));
    float diff = max(0.0, dot(n, L));
    float spec = pow(max(0.0, dot(n, normalize(L + vec3(0.0, 0.0, 1.0)))), 28.0);

    // Color: base tinted palette shifted by the fold height.
    vec3 base = tintedPalette(h * 0.2 + 0.1);
    vec3 col = base * (0.4 + 0.6 * diff);
    col += vec3(1.0, 0.95, 0.9) * spec * sheen * 0.7;
    // Rim light along fold peaks for satin sheen.
    float rim = smoothstep(0.7, 1.0, 1.0 - n.z) * sheen;
    col += tintedPalette(h * 0.2 + 0.4) * rim * 0.3;

    fragColor = vec4(finalize(col), 1.0);
}
