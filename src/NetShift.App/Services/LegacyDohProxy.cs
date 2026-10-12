using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NetShift.App.Services;

/// <summary>Local RFC 8484 UDP-to-HTTPS DNS forwarder used only by Legacy mode.</summary>
internal sealed class LegacyDohProxy : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly UdpClient[] _udp;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public LegacyDohProxy(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Для Legacy нужен HTTPS-адрес DoH.", nameof(endpoint));
        _endpoint = uri;
        var bootstrap = Dns.GetHostAddresses(uri.DnsSafeHost);
        if (bootstrap.Length == 0) throw new InvalidOperationException("Не удалось разрешить адрес DoH до включения Legacy.");
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                Exception? lastError = null;
                foreach (var address in bootstrap)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, uri.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception error) { lastError = error; socket.Dispose(); }
                }
                throw new HttpRequestException("Не удалось подключиться к DoH-серверу.", lastError);
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _udp = [new UdpClient(new IPEndPoint(IPAddress.Loopback, 53)), new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 53))];
        _loop = Task.WhenAll(_udp.Select(udp => RunAsync(udp, _shutdown.Token)));
    }

    private async Task RunAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try { request = await udp.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            _ = ForwardAsync(udp, request, cancellationToken);
        }
    }

    private async Task ForwardAsync(UdpClient udp, UdpReceiveResult request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Buffer.Length < 12) return;
            using var content = new ByteArrayContent(request.Buffer);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
            using var response = await _http.PostAsync(_endpoint, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var answer = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (answer.Length is >= 12 and <= 65507)
                await udp.SendAsync(answer, request.RemoteEndPoint, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { await SendServFailAsync(udp, request, cancellationToken); }
        catch (SocketException) { await SendServFailAsync(udp, request, cancellationToken); }
        catch (IOException) { await SendServFailAsync(udp, request, cancellationToken); }
    }

    private static async Task SendServFailAsync(UdpClient udp, UdpReceiveResult request, CancellationToken cancellationToken)
    {
        if (request.Buffer.Length < 12) return;
        var response = (byte[])request.Buffer.Clone();
        response[2] |= 0x80;
        response[3] = (byte)((response[3] & 0xF0) | 0x02);
        Array.Clear(response, 6, 6);
        try { await udp.SendAsync(response, request.RemoteEndPoint, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var udp in _udp) udp.Dispose();
        try { await _loop; } catch (ObjectDisposedException) { }
        _http.Dispose();
        _shutdown.Dispose();
    }
}
