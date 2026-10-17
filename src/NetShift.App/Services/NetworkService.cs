using System.Net.NetworkInformation;
using NetShift.App.Models;

namespace NetShift.App.Services;

public static class NetworkService
{
    public static IReadOnlyList<NetworkAdapterInfo> GetAdapters(bool includeVpn = false)
    {
        var adapters = new List<NetworkAdapterInfo>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var status = adapter.OperationalStatus;
                if (status != OperationalStatus.Up) continue;

                var isVpn = adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
                            $"{adapter.Name} {adapter.Description}".Contains("vpn", StringComparison.OrdinalIgnoreCase) ||
                            $"{adapter.Name} {adapter.Description}".Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
                            $"{adapter.Name} {adapter.Description}".Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                            $"{adapter.Name} {adapter.Description}".Contains("tap-windows", StringComparison.OrdinalIgnoreCase);
                if (isVpn && !includeVpn) continue;
                var properties = adapter.GetIPProperties();
                var interfaceIndex = properties.GetIPv4Properties()?.Index
                                     ?? properties.GetIPv6Properties()?.Index;
                if (interfaceIndex is null or <= 0)
                    continue;

                adapters.Add(new NetworkAdapterInfo
                {
                    Name = adapter.Name,
                    Description = adapter.Description,
                    Status = "Подключён",
                    InterfaceIndex = interfaceIndex.Value,
                    IsVpn = isVpn
                });
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear while Windows refreshes its network inventory.
            }
        }

        return adapters;
    }
}
