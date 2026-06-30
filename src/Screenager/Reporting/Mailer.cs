using System.Net.Http.Headers;
using System.Text;

namespace Screenager.Reporting;

/// <summary>
/// Sends mail through the Mailgun HTTP API (a single authenticated POST). No SMTP/TLS code,
/// just the built-in <see cref="HttpClient"/>.
/// </summary>
public static class Mailer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<(bool ok, string message)> SendAsync(Config cfg, string subject, string html, string text)
    {
        if (!cfg.MailgunConfigured)
            return (false, "Mailgun is not fully configured (need domain, api_key, from, to).");

        var url = $"{cfg.MailgunApiBase}/v3/{cfg.MailgunDomain}/messages";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{cfg.MailgunApiKey}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = cfg.MailFrom,
            ["to"] = cfg.MailTo,
            ["subject"] = subject,
            ["text"] = text,
            ["html"] = html,
        });

        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, "sent")
                : (false, $"Mailgun returned {(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
