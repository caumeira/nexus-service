uniform float u_speed;
uniform float u_size;  // extra: outer cube half-extent (0.2..1.6)
uniform float u_spin;  // extra: rotation speed multiplier (0.1..4)
uniform float u_glow;  // extra: edge glow thickness (0.2..3)

// Three nested wireframe cubes rendered as signed-distance ray-march in
// the fragment shader. Each cube has its own rotation axis and palette
// offset so the structure reads as 3D layered geometry with varied color.
// Volumetric accumulation of edge glow along each ray; sharper exp
// falloff than a single-cube version so the wires read crisply.

float sdBoxFrame(vec3 p, vec3 b, float e) {
    p = abs(p) - b;
    vec3 q = abs(p + e) - e;
    return min(min(
        length(max(vec3(p.x, q.y, q.z), 0.0)) + min(max(p.x, max(q.y, q.z)), 0.0),
        length(max(vec3(q.x, p.y, q.z), 0.0)) + min(max(q.x, max(p.y, q.z)), 0.0)),
        length(max(vec3(q.x, q.y, p.z), 0.0)) + min(max(q.x, max(q.y, p.z)), 0.0));
}

mat3 rotY(float a) { float c = cos(a); float s = sin(a); return mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c); }
mat3 rotX(float a) { float c = cos(a); float s = sin(a); return mat3(1.0, 0.0, 0.0, 0.0, c, -s, 0.0, s, c); }
mat3 rotZ(float a) { float c = cos(a); float s = sin(a); return mat3(c, -s, 0.0, s, c, 0.0, 0.0, 0.0, 1.0); }

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed;
    float sz = clamp(u_size, 0.2, 1.6);
    float spin = clamp(u_spin, 0.1, 4.0);
    float glowThick = clamp(u_glow, 0.2, 3.0);

    vec3 ro = vec3(0.0, 0.0, -2.5);
    vec3 rd = normalize(vec3(uv, 1.4));

    mat3 rotOuter  = rotY(t * 0.6  * spin) * rotX(t * 0.45 * spin);
    mat3 rotMiddle = rotX(-t * 0.8 * spin) * rotZ(t * 0.5  * spin);
    mat3 rotInner  = rotZ(t * 1.1  * spin) * rotY(-t * 0.7 * spin);

    vec3 col = vec3(0.015, 0.008, 0.03);
    // Bounding-sphere early exit: the cube cluster (outer side sz up to 1.6
    // plus edge thickness 0.085) fits inside radius sqrt(3)*1.6+0.1 ~= 2.87,
    // so R^2 = 8.5 is a safe hull. Pixels whose ray misses the sphere skip
    // the ray-march entirely and just get the dark background.
    float B = dot(ro, rd);
    float C = dot(ro, ro) - 8.5;
    float disc = B * B - C;
    if (disc > 0.0) {
        float sd = sqrt(disc);
        float tNear = max(-B - sd, 0.0);
        float tFar  = -B + sd;
        float sharp = 55.0 / glowThick;
        // March only between the sphere entry and exit points. Same per-step
        // cost as before but the range is tight around the geometry, so 22
        // steps covers the envelope as well as 28 uniform steps did.
        int steps = 22;
        float dt = (tFar - tNear) / float(steps);

        for (int i = 0; i < steps; i++) {
            float d = tNear + (float(i) + 0.5) * dt;
            vec3 p = ro + rd * d;

            vec3 p1 = rotOuter * p;
            float f1 = sdBoxFrame(p1, vec3(sz), 0.085);
            vec3 c1 = tintedPalette(t * 0.08 + p1.x * 0.28 + p1.y * 0.18 + p1.z * 0.14);

            vec3 p2 = rotMiddle * p;
            float f2 = sdBoxFrame(p2, vec3(sz * 0.62), 0.07);
            vec3 c2 = tintedPalette(0.33 + t * 0.11 + p2.x * 0.24 + p2.z * 0.22);

            vec3 p3 = rotInner * p;
            float f3 = sdBoxFrame(p3, vec3(sz * 0.36), 0.06);
            vec3 c3 = tintedPalette(0.66 + t * 0.14 + p3.y * 0.26 + p3.z * 0.20);

            col += c1 * exp(-max(f1, 0.0) * sharp) * 0.085;
            col += c2 * exp(-max(f2, 0.0) * sharp) * 0.085;
            col += c3 * exp(-max(f3, 0.0) * sharp) * 0.085;
        }
    }

    fragColor = vec4(finalize(col), 1.0);
}
