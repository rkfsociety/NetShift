using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NetShift.App.Models;

namespace NetShift.App.Services;

public static class DnsAiProviderService
{
    private const string SetupPageUrl = "https://dns-ai.ru/#setup";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetShift/1.0 (Windows desktop)");
        return client;
    }

    public static async Task<DnsProvider> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var html = await Client.GetStringAsync(SetupPageUrl, cancellationToken);
        var ipv4Primary = ReadValue(html, "v6");
        var ipv4Secondary = ReadValue(html, "v7");
        var ipv6Primary = ReadValue(html, "v8");
        var ipv6Secondary = ReadValue(html, "v9");
        var dohTemplate = ReadValue(html, "v1");

        RequireAddress(ipv4Primary, AddressFamily.InterNetwork);
        RequireAddress(ipv4Secondary, AddressFamily.InterNetwork);
        RequireAddress(ipv6Primary, AddressFamily.InterNetworkV6);
        RequireAddress(ipv6Secondary, AddressFamily.InterNetworkV6);
        if (!Uri.TryCreate(dohTemplate, UriKind.Absolute, out var dohUri) ||
            dohUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(dohUri.UserInfo) || !string.IsNullOrEmpty(dohUri.Fragment))
            throw new InvalidDataException("На странице DNS-AI найден некорректный шаблон DoH.");

        return new DnsProvider("DNS-AI", $"{ipv4Primary}, {ipv4Secondary}", $"{ipv6Primary}, {ipv6Secondary}", dohTemplate);
    }

    private static string ReadValue(string html, string id)
    {
        var idPattern = $@"\bid\s*=\s*(['""]){Regex.Escape(id)}\1";
        var spanPattern = $"<span\\b(?=[^>]*{idPattern})[^>]*>(?<value>.*?)</span>";
        var match = Regex.Match(html, spanPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidDataException($"На странице DNS-AI не найдено значение {id}.");

        var value = Regex.Replace(match.Groups["value"].Value, "<[^>]+>", string.Empty).Trim();
        value = WebUtility.HtmlDecode(value);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"На странице DNS-AI пустое значение {id}.");
        return value;
    }

    private static void RequireAddress(string value, AddressFamily expectedFamily)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != expectedFamily)
            throw new InvalidDataException("DNS-AI опубликовал некорректный IP-адрес.");
    }
}
