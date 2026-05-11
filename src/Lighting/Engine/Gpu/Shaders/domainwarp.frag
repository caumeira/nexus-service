uniform float u_speed;
uniform float u_turbulence; // extra: warp intensity
uniform float u_direction;  // extra: flow direction angle
uniform float u_bite;       // extra: contrast curve (renamed to avoid u_contrast clash)

// Inigo Quilez recursive domain warp. Two fbm passes displace the sample
// coordinate before the final fbm read, producing the smoky, continent-like
// macrostructure that domain warping is famous for.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.95, 1000.0);
    float turb = max(0.2, u_turbulence);
    float dir = u_direction;
    float bite = max(0.3, u_bite);

    vec2 p = uv * 1.4;
    vec2 flow = vec2(cos(dir), sin(dir)) * 0.3;

    vec2 q = vec2(
        fbm(p + vec2(t * 0.2, 0.0) + flow),
        fbm(p + vec2(5.2, 1.3) - flow)
    );
    vec2 r = vec2(
        fbm(p + turb * q + vec2(1.7, 9.2) + flow * 2.0 + t * 0.15),
        fbm(p + turb * q + vec2(8.3, 2.8) - flow * 2.0 + t * 0.12)
    );
    float n = fbm(p + turb * r);
    n = pow(clamp(n, 0.0, 1.0), bite);

    vec3 col = tintedPalette(n * 0.6 + 0.05);
    col *= 0.3 + 1.2 * smoothstep(0.1, 0.9, n);

    fragColor = vec4(finalize(col), 1.0);
}
