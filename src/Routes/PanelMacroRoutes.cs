using System.Diagnostics;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Routes;

public static class PanelMacroRoutes
{
    public static void MapPanelMacroRoutes(this WebApplication app)
    {
        app.MapPost("/panel/macros/open-url", (OpenUrlRequest body) =>
        {
            var url = body.Url?.Trim() ?? "";

            if (string.IsNullOrEmpty(url))
            {
                return ApiResponse.Fail("url is required");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return ApiResponse.Fail("invalid url - must be an absolute http or https URL");
            }

            try
            {
                Process.Start(new ProcessStartInfo(parsed.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
                return ApiResponse.Ok("opened");
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail($"failed to open url: {ex.Message}");
            }
        }).AllowPanel();

        app.MapPost("/panel/macros/shortcut", (ShortcutRequest body) =>
        {
            var keys = body.Keys?.Trim() ?? "";

            if (string.IsNullOrEmpty(keys))
            {
                return ApiResponse.Fail("keys is required");
            }

            // Keyboard simulation is not yet implemented - stub endpoint
            return ApiResponse.Ok("shortcut received (simulation not yet implemented)");
        }).AllowPanel();
    }
}
