using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Static mode renders one frame and holds it, so every catalog shader must be
/// a pure function of position. These pin that against the GLSL itself.
/// </summary>
public class StaticEffectCatalogTests
{
    [Fact]
    public void DefaultEffect_IsTheLinearGradient()
    {
        // A key outside the catalog would persist through StartStatic("") and
        // render as rainbow via BuildAnimateEffect's fallthrough.
        Assert.Equal("gradientlinear", StaticEffectCatalog.DefaultEffect);
        Assert.True(StaticEffectCatalog.Contains(StaticEffectCatalog.DefaultEffect));
    }

    [Fact]
    public void Patterns_ReferenceNoTimeAtAll()
    {
        // These are purpose-built static shaders, not animate shaders held at
        // speed 0: a time read of any kind would animate the LEDs while the
        // engine served one held canvas.
        foreach (var key in StaticEffectCatalog.Patterns)
        {
            var body = ShaderBody(key);
            Assert.False(body.Contains("u_time", StringComparison.Ordinal),
                $"{key}.frag reads u_time; static patterns must be a pure function of position");
            Assert.False(body.Contains("u_speed", StringComparison.Ordinal),
                $"{key}.frag reads u_speed; static patterns have no clock to scale");
        }
    }

    [Fact]
    public void Patterns_StayCheapToRender()
    {
        // Static output is rendered once and held, but a pathological shader
        // would still stall the first frame; these are meant to be flat ALU.
        foreach (var key in StaticEffectCatalog.Patterns)
        {
            var body = ShaderBody(key);
            Assert.False(body.Contains("fbm(", StringComparison.Ordinal), $"{key}.frag uses fbm");
            Assert.False(body.Contains("vnoise(", StringComparison.Ordinal), $"{key}.frag uses vnoise");
            Assert.DoesNotContain("for (", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Patterns_Resolve_To_Their_Own_Distinct_Shader()
    {
        // BuildAnimateEffect's switch falls back to rainbow for unknown names,
        // so a pattern missing its own source renders as rainbow on every
        // surface while still returning a valid 200 thumbnail.
        var rainbow = ShaderLibrary.Get("rainbow");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in StaticEffectCatalog.Patterns)
        {
            var src = ShaderLibrary.Get(key);
            Assert.NotEqual(rainbow, src);
            Assert.False(sources.TryGetValue(src, out var twin),
                $"{key}.frag is byte-identical to {twin}.frag");
            sources[src] = key;
        }
    }

    [Fact]
    public void Every_catalog_key_is_fetchable_by_the_client()
    {
        // /lighting/shaders/{name} gates on AllEffectKeys; a key missing there
        // cannot be rendered locally, so the canvas silently falls back to the
        // low-res streamed LED preview.
        foreach (var key in StaticEffectCatalog.Patterns.Concat(StaticEffectCatalog.Fills))
        {
            Assert.Contains(key, ShaderLibrary.AllEffectKeys);
        }
    }

    [Fact]
    public void Fills_UseTheTimeFreeShader()
    {
        foreach (var key in StaticEffectCatalog.Fills)
        {
            Assert.DoesNotContain("u_time", ShaderBody(key), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_HasNoDuplicateKeys()
    {
        var all = StaticEffectCatalog.Fills.Concat(StaticEffectCatalog.Patterns).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Contains_MatchesBothGroups()
    {
        Assert.True(StaticEffectCatalog.Contains("simplered"));
        Assert.True(StaticEffectCatalog.IsFill("simplered"));
        Assert.True(StaticEffectCatalog.Contains("gradientlinear"));
        Assert.False(StaticEffectCatalog.IsFill("gradientlinear"));
        // Animate effects stay out: static is its own shader set now.
        Assert.False(StaticEffectCatalog.Contains("rainbow"));
        Assert.False(StaticEffectCatalog.Contains("fire"));
    }

    // The prelude is shared and grows over time, so key off its end marker
    // rather than a specific helper name: a function added after the marker
    // would otherwise leak prelude text into every assertion below.
    private const string PreludeEndMarker = "// ---- effect body ----";

    private static string ShaderBody(string key)
    {
        var src = ShaderLibrary.Get(key);
        var marker = src.IndexOf(PreludeEndMarker, StringComparison.Ordinal);
        Assert.True(marker >= 0, $"prelude end marker missing; {PreludeEndMarker} must terminate _prelude.frag");
        return src[(marker + PreludeEndMarker.Length)..];
    }
}
