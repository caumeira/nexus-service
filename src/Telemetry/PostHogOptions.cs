using System;

namespace Nexus.Service.Telemetry;

/// <summary>
/// PostHog ingestion config. The Project API Key is the only required value and
/// is a PUBLIC, write-only key - safe to commit and ship in the client.
///
/// Resolution order: the NEXUS_POSTHOG_KEY environment variable (handy for
/// local testing), else the baked <see cref="DefaultProjectApiKey"/> below.
/// While both are empty the telemetry sink stays a no-op.
/// </summary>
internal sealed class PostHogOptions
{
    // Paste your Project API Key here (PostHog → Settings → Project). Looks like
    // "phc_xxx". Public + write-only - committing it is expected and safe.
    private const string DefaultProjectApiKey = "phc_REDACTED";

    /// <summary>US data residency. EU projects use "eu.i.posthog.com".</summary>
    public string Host { get; init; } = "us.i.posthog.com";

    public string ProjectApiKey { get; init; } =
        Environment.GetEnvironmentVariable("NEXUS_POSTHOG_KEY") is { Length: > 0 } env
            ? env
            : DefaultProjectApiKey;

    public string BatchEndpoint => $"https://{Host}/batch/";
}
