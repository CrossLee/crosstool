using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Crosio.Windows.Sharing;

public static class LocalNetworkAddresses
{
    public static IReadOnlyList<IPAddress> GetUsableIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and
                not NetworkInterfaceType.Tunnel)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Where(address => !IPAddress.IsLoopback(address.Address))
            .Where(address => !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.Address)
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
}
