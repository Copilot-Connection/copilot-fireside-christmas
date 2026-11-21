namespace Fireside;

public sealed class BrowserOrigin(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly string? configured = GetConfiguredOrigin(configuration, environment);

    private static string? GetConfiguredOrigin(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var value = configuration["Web:AllowedOrigin"];
        if (string.IsNullOrWhiteSpace(value) && configuration["WEBSITE_HOSTNAME"] is { Length: > 0 } host)
            value = $"https://{host}";
        if (string.IsNullOrWhiteSpace(value))
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return null;
            throw new InvalidOperationException("Configure Web:AllowedOrigin or run on Azure App Service.");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || value.TrimEnd('/') != uri.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Web:AllowedOrigin must be an HTTPS origin without a path.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public bool Allows(HttpRequest request)
    {
        var expected = configured ?? $"{request.Scheme}://{request.Host}";
        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Count > 1 || fetchSite.ToString() is "cross-site" or "same-site") return false;
        var origin = request.Headers.Origin;
        if (origin.Count > 0) return origin.Count == 1 && string.Equals(origin[0], expected, StringComparison.OrdinalIgnoreCase);
        var referer = request.Headers.Referer;
        return referer.Count == 1 && Uri.TryCreate(referer[0], UriKind.Absolute, out var uri) &&
            uri.UserInfo.Length == 0 &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), expected, StringComparison.OrdinalIgnoreCase);
    }
}
