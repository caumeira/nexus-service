uniform float u_aHue;
uniform float u_aSat;
uniform float u_aVal;
uniform float u_bHue;
uniform float u_bSat;
uniform float u_bVal;
uniform float u_cHue;
uniform float u_cSat;
uniform float u_cVal;
uniform float u_dHue;
uniform float u_dSat;
uniform float u_dVal;

// Four corner colours blended bilinearly - the widest colour spread of the set.
void main() {
    vec2 p = uv01();
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    vec3 cc = hsv2rgb(vec3(u_cHue, u_cSat, u_cVal));
    vec3 cd = hsv2rgb(vec3(u_dHue, u_dSat, u_dVal));
    vec3 top = mix(ca, cb, p.x);
    vec3 bot = mix(cc, cd, p.x);
    fragColor = vec4(mix(top, bot, p.y), 1.0);
}
