namespace NetShift.App.Models;

public sealed record DnsProvider(string Name, string Ipv4, string Ipv6, string? DohTemplate = null)
{
    public bool RequiresManualAddressInput => string.IsNullOrWhiteSpace(Ipv4) && string.IsNullOrWhiteSpace(Ipv6);

    public override string ToString() => Name;

    public static DnsProvider DnsAi { get; } = new(
        "DNS-AI",
        "192.144.59.14, 186.246.49.127",
        "2a0d:8480:0:67c::14, 2a0a:2b41:0:500d::53",
        "https://dns.dns-ai.ru/dns-query");

    public static IReadOnlyList<DnsProvider> Presets { get; } =
    [
        new("Cloudflare", "1.1.1.1, 1.0.0.1", "2606:4700:4700::1111, 2606:4700:4700::1001"),
        new("Google Public DNS", "8.8.8.8, 8.8.4.4", "2001:4860:4860::8888, 2001:4860:4860::8844"),
        new("Quad9", "9.9.9.9, 149.112.112.112", "2620:fe::fe, 2620:fe::9"),
        new("AdGuard DNS", "94.140.14.14, 94.140.15.15", "2a10:50c0::ad1:ff, 2a10:50c0::ad2:ff"),
        new("Comss.one", "83.220.169.155, 212.109.195.93", "", "https://dns.comss.one/dns-query"),
        new("Xbox DNS", "111.88.96.54, 111.88.96.55", "2a00:ab00:1233:26::50, 2a00:ab00:1233:26::51", "https://xbox-dns.ru/dns-query"),
        DnsAi,
        new("Свой DNS", "", "")
    ];
}
