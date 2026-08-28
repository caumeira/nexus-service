using System;

namespace Nexus.Service.Telemetry;

/// <summary>
/// PostHog ingestion config. The Project API Key is write-only, but it is OUR
/// project: a fork shipping it would file its installs under our analytics, so
/// it is injected at build time (NexusPostHogKey) rather than committed.
///
/// Resolution order: the NEXUS_POSTHOG_KEY environment variable (handy for
/// local testing), else the build-injected key. While both are empty the
/// telemetry sink stays a no-op, which is what an unofficial build gets.
/// </summary>
internal sealed class PostHogOptions
{
    private const string DefaultProjectApiKey = BuildInfo.PostHogKey;

    /// <summary>US data residency. EU projects use "eu.i.posthog.com".</summary>
    public string Host { get; init; } = "us.i.posthog.com";

    public string ProjectApiKey { get; init; } =
        Environment.GetEnvironmentVariable("NEXUS_POSTHOG_KEY") is { Length: > 0 } env
            ? env
            : DefaultProjectApiKey;

    public string BatchEndpoint => $"https://{Host}/batch/";
}
