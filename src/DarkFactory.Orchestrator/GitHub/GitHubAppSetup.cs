using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.GitHub;

public sealed record CreatedApp(long Id, string Slug, string HtmlUrl);

/// <summary>
/// GitHub App manifest flow (https://docs.github.com/apps/sharing-github-apps/registering-a-github-app-from-a-manifest):
/// a localhost page POSTs the committed manifest to github.com, GitHub redirects back with a
/// one-time code, and the code is exchanged for the app id and private key, which go to the keychain.
/// </summary>
public sealed class GitHubAppSetup(HttpClient http, ISecretStore secrets, TextWriter output)
{
    public const string NewAppUrl = "https://github.com/settings/apps/new";

    public static string BuildManifest(string appName, int port)
    {
        using var stream = typeof(GitHubAppSetup).Assembly.GetManifestResourceStream("app-manifest.json")
            ?? throw new InvalidOperationException("Embedded app-manifest.json is missing.");
        var manifest = JsonNode.Parse(stream)!.AsObject();
        manifest["name"] = appName;
        manifest["redirect_url"] = $"http://localhost:{port}/callback";
        return manifest.ToJsonString();
    }

    public static string RenderStartPage(string manifestJson, string state) =>
        $"""
        <!doctype html>
        <html><body>
        <p>Registering the Dark Factory GitHub App&hellip;</p>
        <form id="f" method="post" action="{NewAppUrl}?state={WebUtility.UrlEncode(state)}">
          <input type="hidden" name="manifest" value="{WebUtility.HtmlEncode(manifestJson)}">
          <button type="submit">Continue to GitHub</button>
        </form>
        <script>document.getElementById('f').submit();</script>
        </body></html>
        """;

    /// <summary>Exchanges the manifest code and stores the credentials. Never writes the key anywhere else.</summary>
    public async Task<CreatedApp> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app-manifests/{Uri.EscapeDataString(code)}/conversions");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("dark-factory/0.1");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, "manifest conversion", ct);
        var app = await response.Content.ReadFromJsonAsync<ConversionDto>(ct)
            ?? throw new InvalidOperationException("Empty manifest conversion response.");

        secrets.Set(SecretAccounts.GitHubAppId, app.Id.ToString());
        secrets.Set(SecretAccounts.GitHubAppSlug, app.Slug);
        secrets.Set(SecretAccounts.GitHubAppPrivateKey, app.Pem);
        return new CreatedApp(app.Id, app.Slug, app.HtmlUrl);
    }

    public async Task<CreatedApp> RunAsync(string appName, int port, CancellationToken ct)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var manifest = BuildManifest(appName, port);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var startUrl = $"http://localhost:{port}/";
        output.WriteLine($"Opening {startUrl} — approve the app on GitHub in your browser.");
        Process.Start(new ProcessStartInfo("open", startUrl) { UseShellExecute = false })?.Dispose();

        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/")
            {
                await Respond(context, RenderStartPage(manifest, state));
                continue;
            }
            if (path != "/callback")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            var code = context.Request.QueryString["code"];
            if (context.Request.QueryString["state"] != state || string.IsNullOrEmpty(code))
            {
                await Respond(context, "State mismatch or missing code; ignoring.", 400);
                continue;
            }

            var app = await ExchangeCodeAsync(code, ct);
            var installUrl = $"https://github.com/apps/{app.Slug}/installations/new";
            await Respond(context,
                $"<p>App <b>{WebUtility.HtmlEncode(app.Slug)}</b> created. Now <a href=\"{installUrl}\">install it</a> on the target repositories.</p>");
            output.WriteLine($"Created GitHub App '{app.Slug}' (id {app.Id}); credentials stored in the login keychain (service '{SecretAccounts.Service}').");
            output.WriteLine($"Next: install it on the target repo(s): {installUrl}");
            return app;
        }
    }

    private static async Task Respond(HttpListenerContext context, string html, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private sealed record ConversionDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("pem")] string Pem);
}
