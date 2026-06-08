using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace RitschyMirror;

/// <summary>
/// Fragt das neueste GitHub-Release ab und vergleicht es mit der laufenden Version.
/// Reiner Hinweis (kein Selbst-Update): „Version X ist da → Release öffnen".
/// Best-effort — bei offline/Fehlern/Rate-Limit gibt es einfach `null` zurück.
/// </summary>
internal static class UpdateCheck
{
    public sealed record Result(bool UpdateAvailable, string LatestVersion, string Url);

    private const string Api = "https://api.github.com/repos/RitschyRigz/ritschy-mirror/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub verlangt einen User-Agent, sonst 403.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RitschyMirror", AppInfo.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static async Task<Result?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Api).ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? AppInfo.RepoUrl) : AppInfo.RepoUrl;
            var latest = ParseVersion(tag);
            var current = ParseVersion(AppInfo.Version);
            if (latest is null || current is null) return null;
            return new Result(latest > current, $"{latest.Major}.{latest.Minor}.{latest.Build}", url);
        }
        catch { return null; }
    }

    /// <summary>„v1.0.2", „1.0.2-beta", „1.0.2+hash" → 1.0.2.0 (Major.Minor.Build).</summary>
    private static Version? ParseVersion(string s)
    {
        s = (s ?? "").Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
    }
}
