namespace Helldivers2ModManager.Core.BrowserIntegration;

public static class NexusDownloadUrlPolicy
{
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.IdnHost;
        return string.Equals(host, "files.nexus-cdn.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase);
    }
}
