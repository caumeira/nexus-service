using System;

namespace Nexus.Service.Lighting;

/// <summary>
/// Keys the Static mode offers. Mirrors STATIC_FILL_KEYS / STATIC_PATTERN_KEYS
/// in nexus-web/src/types/lighting.ts.
/// </summary>
public static class StaticEffectCatalog
{
    /// <summary>Key Static mode lands on before the user picks anything. Mirrors
    /// DEFAULT_STATIC_EFFECT in nexus-web/src/types/lighting.ts.</summary>
    public const string DefaultEffect = "gradientlinear";

    /// <summary>Solid-colour fills; every key resolves to the one simple.frag fill shader.</summary>
    public static readonly string[] Fills =
    {
        "simplewhite", "simplesoftpink", "simplepink", "simplered", "simpleorange",
        "simpleyellow", "simplegreen", "simpledarkgreen", "simpleturquoise", "simplecyan",
        "simpleblue", "simpleviolet",
    };

    /// <summary>
    /// Purpose-built static patterns. Each one is a pure function of position -
    /// no u_time at all, so there is nothing to freeze - and carries its own
    /// colours rather than the global tint. StaticEffectCatalogTests enforces
    /// the no-time rule against the GLSL.
    /// </summary>
    public static readonly string[] Patterns =
    {
        "gradientlinear", "gradientradial", "gradienttri", "gradientconic",
        "mirror", "corners",
        "splitsharp", "stripes", "checker", "border", "rings", "dots", "wedges",
        "spectrumramp", "spectrumbands", "huewheel",
    };

    public static bool IsFill(string key) => Array.IndexOf(Fills, key) >= 0;

    public static bool Contains(string key) => IsFill(key) || Array.IndexOf(Patterns, key) >= 0;
}
