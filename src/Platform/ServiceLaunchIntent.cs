namespace Qos.Service.Platform;

public static class ServiceLaunchIntent
{
    public const int DefaultServicePort = 9400;
    public const string DefaultBindUrl = "http://0.0.0.0:9400";

    public static string ResolveServiceUrl(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return DefaultBindUrl;
        }

        var candidate = args[0];
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.StartsWith("qos://", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultBindUrl;
        }

        return candidate;
    }

    public static int ResolveServicePort(string serviceUrl)
    {
        return Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) && uri.Port > 0
            ? uri.Port
            : DefaultServicePort;
    }

    public static string LocalDashboardUrl(int servicePort)
    {
        var port = servicePort > 0 ? servicePort : DefaultServicePort;
        return $"http://localhost:{port}/";
    }
}
