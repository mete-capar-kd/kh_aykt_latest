using System.Net;
using System.Net.Sockets;

namespace Hackathon.Assessment.Api.Snapshot;

public static class IpAddressPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is not (0 or 10 or 127)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] is 0 or 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return (bytes[0] & 0xE0) == 0x20
                && !(bytes[0] == 0x20 && bytes[1] == 0x01
                    && bytes[2] == 0x0D && bytes[3] == 0xB8)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0)
                && !(bytes[0] == 0x20 && bytes[1] == 0x02);
        }

        return false;
    }

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        }
        catch (SocketException)
        {
            throw new HttpRequestException("Repository host resolution failed.");
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
        {
            throw new HttpRequestException("Repository host address is not public.");
        }

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                if (socket.RemoteEndPoint is not IPEndPoint remote || !IsPublic(remote.Address))
                {
                    throw new HttpRequestException("Connected repository address is not public.");
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
                // Try the next already-validated DNS address.
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Repository host connection failed.");
    }
}
