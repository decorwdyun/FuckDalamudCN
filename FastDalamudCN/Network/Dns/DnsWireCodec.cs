using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FastDalamudCN.Network.Dns;

internal readonly record struct DnsWireResponse(int ResponseCode, IPAddress[] Addresses, TimeSpan? MinTtl);

internal static class DnsWireCodec
{
    public const int NoError = 0;
    public const int ServerFailure = 2;
    public const int NameError = 3;
    public const int Refused = 5;

    private const int HeaderSize = 12;
    private const int MaxWireNameLength = 255;
    private const int MaxLabelLength = 63;
    private const int MaxPointerHops = 64;
    private const ushort TypeA = 1;
    private const ushort TypeCname = 5;
    private const ushort TypeAaaa = 28;
    private const ushort ClassIn = 1;
    private static readonly IdnMapping IdnMapping = new();

    public static byte[] BuildQuery(string hostname, AddressFamily addressFamily)
    {
        var queryType = addressFamily switch
        {
            AddressFamily.InterNetwork => TypeA,
            AddressFamily.InterNetworkV6 => TypeAaaa,
            _ => throw new NotSupportedException($"不支持的地址族: {addressFamily}")
        };

        var normalized = NormalizeHostname(hostname);
        var labels = normalized.Split('.');
        var wireNameLength = 1; // root terminator
        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength)
            {
                throw new ArgumentException($"无效的 DNS 域名标签: {hostname}", nameof(hostname));
            }

            wireNameLength += 1 + label.Length;
        }

        if (wireNameLength > MaxWireNameLength)
        {
            throw new ArgumentException($"DNS 域名超过最大长度限制: {hostname}", nameof(hostname));
        }

        var buffer = new byte[HeaderSize + wireNameLength + 4];
        // RFC 8484 §4.1: ID = 0 for HTTP cacheability; Flags = 0x0100 (RD = 1); QDCOUNT = 1
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), 1);

        var offset = HeaderSize;
        foreach (var label in labels)
        {
            buffer[offset++] = (byte)label.Length;
            var written = Encoding.ASCII.GetBytes(label, buffer.AsSpan(offset, label.Length));
            if (written != label.Length)
            {
                throw new ArgumentException($"DNS 域名包含非法字符: {hostname}", nameof(hostname));
            }

            offset += written;
        }

        buffer[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), queryType);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 2, 2), ClassIn);

        return buffer;
    }

    public static DnsWireResponse ParseResponse(ReadOnlySpan<byte> packet, AddressFamily expectedFamily)
    {
        var expectedType = expectedFamily switch
        {
            AddressFamily.InterNetwork => TypeA,
            AddressFamily.InterNetworkV6 => TypeAaaa,
            _ => throw new NotSupportedException($"不支持的地址族: {expectedFamily}")
        };

        var expectedDataLength = expectedFamily == AddressFamily.InterNetwork ? 4 : 16;

        if (packet.Length < HeaderSize)
        {
            throw new FormatException("DNS 响应报文长度不足。");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if ((flags & 0x8000) == 0)
        {
            throw new FormatException("DNS 报文不是响应包（QR=0）。");
        }

        var responseCode = flags & 0x000F;
        if (responseCode != NoError)
        {
            return new DnsWireResponse(responseCode, [], null);
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));

        var offset = HeaderSize;
        for (var i = 0; i < qdCount; i++)
        {
            SkipName(packet, ref offset);
            if (offset + 4 > packet.Length)
            {
                throw new FormatException("DNS Question 段意外截断。");
            }

            offset += 4;
        }

        var addresses = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        uint? minTtlSeconds = null;

        for (var i = 0; i < anCount; i++)
        {
            SkipName(packet, ref offset);
            if (offset + 10 > packet.Length)
            {
                throw new FormatException("DNS Answer 记录头意外截断。");
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            var rrClass = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
            var rawTtl = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(offset + 4, 4));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > packet.Length)
            {
                throw new FormatException("DNS Answer RDATA 意外截断。");
            }

            if (rrClass == ClassIn)
            {
                // RFC 2181 §8: TTL 最高位视为 0
                var ttl = rawTtl & 0x7FFF_FFFF;

                if (type == expectedType && rdLength == expectedDataLength)
                {
                    var address = new IPAddress(packet.Slice(offset, expectedDataLength));
                    if (seen.Add(address))
                    {
                        addresses.Add(address);
                    }

                    minTtlSeconds = minTtlSeconds is null ? ttl : Math.Min(minTtlSeconds.Value, ttl);
                }
                else if (type == TypeCname)
                {
                    minTtlSeconds = minTtlSeconds is null ? ttl : Math.Min(minTtlSeconds.Value, ttl);
                }
            }

            offset += rdLength;
        }

        var minTtl = addresses.Count > 0 && minTtlSeconds is not null
            ? TimeSpan.FromSeconds(minTtlSeconds.Value)
            : (TimeSpan?)null;

        return new DnsWireResponse(NoError, addresses.ToArray(), minTtl);
    }

    private static string NormalizeHostname(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new ArgumentException("域名不能为空。", nameof(hostname));
        }

        var trimmed = hostname.Trim().TrimEnd('.');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("域名不能为空。", nameof(hostname));
        }

        var ascii = IdnMapping.GetAscii(trimmed);
        foreach (var ch in ascii)
        {
            if (ch > 0x7F || char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                throw new ArgumentException($"DNS 域名包含非法字符: {hostname}", nameof(hostname));
            }
        }

        return ascii;
    }

    private static void SkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var pos = offset;
        var jumped = false;
        var hops = 0;
        var totalLength = 0;

        while (true)
        {
            if (pos >= packet.Length)
            {
                throw new FormatException("DNS 名称意外截断。");
            }

            var length = packet[pos];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = pos + 1;
                }

                return;
            }

            var prefix = length & 0xC0;
            if (prefix == 0xC0)
            {
                if (pos + 1 >= packet.Length)
                {
                    throw new FormatException("DNS 压缩指针截断。");
                }

                if (++hops > MaxPointerHops)
                {
                    throw new FormatException("DNS 压缩指针超过最大跳转限制。");
                }

                var pointer = ((length & 0x3F) << 8) | packet[pos + 1];
                if (pointer >= packet.Length)
                {
                    throw new FormatException("DNS 压缩指针越界。");
                }

                if (!jumped)
                {
                    offset = pos + 2;
                    jumped = true;
                }

                pos = pointer;
                continue;
            }

            if (prefix != 0)
            {
                throw new FormatException($"不支持的 DNS 标签类型: 0x{length:X2}。");
            }

            totalLength += 1 + length;
            if (totalLength >= MaxWireNameLength)
            {
                throw new FormatException("DNS 名称超过最大长度限制。");
            }

            pos += 1 + length;
            if (pos > packet.Length)
            {
                throw new FormatException("DNS 标签长度越界。");
            }
        }
    }
}
