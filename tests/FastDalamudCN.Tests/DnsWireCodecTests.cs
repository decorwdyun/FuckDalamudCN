using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FastDalamudCN.Network.Dns;
using Xunit;

namespace FastDalamudCN.Tests;

public class DnsWireCodecTests
{
    [Theory]
    [InlineData("repo.example", AddressFamily.InterNetwork, 1)]
    [InlineData("repo.example.", AddressFamily.InterNetwork, 1)]
    [InlineData("repo.example", AddressFamily.InterNetworkV6, 28)]
    public void BuildQueryEncodesStandardIpv4AndIpv6Queries(string host, AddressFamily family, ushort expectedQType)
    {
        var query = DnsWireCodec.BuildQuery(host, family);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6, 2)));

        // [4]repo[7]example[0]
        var expectedName = new byte[] { 4, (byte)'r', (byte)'e', (byte)'p', (byte)'o', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 0 };
        Assert.Equal(expectedName, query.AsSpan(12, expectedName.Length).ToArray());

        var trailerOffset = 12 + expectedName.Length;
        Assert.Equal(expectedQType, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(trailerOffset, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(trailerOffset + 2, 2)));
        Assert.Equal(trailerOffset + 4, query.Length);
    }

    [Fact]
    public void BuildQueryEncodesIdnHostnameToPunycode()
    {
        var query = DnsWireCodec.BuildQuery("测试.example", AddressFamily.InterNetwork);
        var ascii = Encoding.ASCII.GetString(query, 13, query[12]);

        Assert.Equal("xn--0zwm56d", ascii);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("a..example")]
    public void BuildQueryRejectsInvalidHostnames(string host)
    {
        Assert.Throws<ArgumentException>(() => DnsWireCodec.BuildQuery(host, AddressFamily.InterNetwork));
    }

    [Fact]
    public void BuildQueryRejectsOverlongLabelAndUnsupportedFamily()
    {
        var overlong = new string('a', 64) + ".example";
        Assert.Throws<ArgumentException>(() => DnsWireCodec.BuildQuery(overlong, AddressFamily.InterNetwork));
        Assert.Throws<NotSupportedException>(() => DnsWireCodec.BuildQuery("repo.example", AddressFamily.Unix));
    }

    [Fact]
    public void ParseResponseExtractsDistinctAddressesCnameCompressionAndMinTtl()
    {
        var packet = BuildResponsePacket(
            hostname: "repo.example",
            rcode: DnsWireCodec.NoError,
            answers:
            [
                // CNAME with compressed owner pointing to offset 12, TTL 45s
                BuildCnameRecord(ownerPointerOffset: 12, ttl: 45, targetName: "edge.example"),
                // A record with TTL 120s
                BuildAddressRecord(ownerPointerOffset: 12, ttl: 120, IPAddress.Parse("104.16.0.1")),
                // Duplicate A record with TTL 60s
                BuildAddressRecord(ownerPointerOffset: 12, ttl: 60, IPAddress.Parse("104.16.0.1")),
                // Second distinct A record with MSB-set TTL (RFC 2181: masked to 35s)
                BuildAddressRecord(ownerPointerOffset: 12, ttl: 0x8000_0023, IPAddress.Parse("104.16.0.2"))
            ]);

        var response = DnsWireCodec.ParseResponse(packet, AddressFamily.InterNetwork);

        Assert.Equal(DnsWireCodec.NoError, response.ResponseCode);
        Assert.Equal([IPAddress.Parse("104.16.0.1"), IPAddress.Parse("104.16.0.2")], response.Addresses);
        Assert.Equal(TimeSpan.FromSeconds(35), response.MinTtl);
    }

    [Fact]
    public void ParseResponseSupportsIpv6Records()
    {
        var ipv6 = IPAddress.Parse("2606:4700:4700::1111");
        var packet = BuildResponsePacket(
            hostname: "repo.example",
            rcode: DnsWireCodec.NoError,
            answers: [BuildAddressRecord(ownerPointerOffset: 12, ttl: 90, ipv6)],
            queryType: 28);

        var response = DnsWireCodec.ParseResponse(packet, AddressFamily.InterNetworkV6);

        Assert.Equal(DnsWireCodec.NoError, response.ResponseCode);
        Assert.Equal([ipv6], response.Addresses);
        Assert.Equal(TimeSpan.FromSeconds(90), response.MinTtl);
    }

    [Theory]
    [InlineData(DnsWireCodec.NameError)]
    [InlineData(DnsWireCodec.ServerFailure)]
    [InlineData(DnsWireCodec.Refused)]
    public void ParseResponseReturnsEmptyForNonZeroResponseCodes(int rcode)
    {
        var packet = BuildResponsePacket("repo.example", rcode, []);

        var response = DnsWireCodec.ParseResponse(packet, AddressFamily.InterNetwork);

        Assert.Equal(rcode, response.ResponseCode);
        Assert.Empty(response.Addresses);
        Assert.Null(response.MinTtl);
    }

    [Fact]
    public void ParseResponseRejectsMalformedAndCyclicCompressionPackets()
    {
        // Too short
        Assert.Throws<FormatException>(() => DnsWireCodec.ParseResponse(new byte[8], AddressFamily.InterNetwork));

        // QR = 0 (query instead of response)
        var query = DnsWireCodec.BuildQuery("repo.example", AddressFamily.InterNetwork);
        Assert.Throws<FormatException>(() => DnsWireCodec.ParseResponse(query, AddressFamily.InterNetwork));

        // Self-referencing compression pointer in Question section (offset 12 -> 0xC0 0x0C)
        var cyclic = new byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(cyclic.AsSpan(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(cyclic.AsSpan(4, 2), 1);
        cyclic[12] = 0xC0;
        cyclic[13] = 0x0C;
        Assert.Throws<FormatException>(() => DnsWireCodec.ParseResponse(cyclic, AddressFamily.InterNetwork));

        // Truncated RDATA
        var valid = BuildResponsePacket(
            "repo.example",
            DnsWireCodec.NoError,
            [BuildAddressRecord(12, 60, IPAddress.Parse("1.2.3.4"))]);
        Assert.Throws<FormatException>(() => DnsWireCodec.ParseResponse(valid.AsSpan(0, valid.Length - 2), AddressFamily.InterNetwork));
    }

    internal static byte[] BuildResponsePacket(
        string hostname,
        int rcode,
        IReadOnlyList<byte[]> answers,
        ushort queryType = 1)
    {
        using var stream = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(0x8180 | (rcode & 0x0F)));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)answers.Count);
        stream.Write(header);

        WriteName(stream, hostname);
        WriteUInt16(stream, queryType);
        WriteUInt16(stream, 1);

        foreach (var answer in answers)
        {
            stream.Write(answer);
        }

        return stream.ToArray();
    }

    internal static byte[] BuildAddressRecord(ushort ownerPointerOffset, uint ttl, IPAddress address)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, (ushort)(0xC000 | ownerPointerOffset));
        var type = address.AddressFamily == AddressFamily.InterNetwork ? (ushort)1 : (ushort)28;
        var bytes = address.GetAddressBytes();
        WriteUInt16(stream, type);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, ttl);
        WriteUInt16(stream, (ushort)bytes.Length);
        stream.Write(bytes);
        return stream.ToArray();
    }

    private static byte[] BuildCnameRecord(ushort ownerPointerOffset, uint ttl, string targetName)
    {
        using var nameStream = new MemoryStream();
        WriteName(nameStream, targetName);
        var rdata = nameStream.ToArray();

        using var stream = new MemoryStream();
        WriteUInt16(stream, (ushort)(0xC000 | ownerPointerOffset));
        WriteUInt16(stream, 5);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, ttl);
        WriteUInt16(stream, (ushort)rdata.Length);
        stream.Write(rdata);
        return stream.ToArray();
    }

    private static void WriteName(Stream stream, string hostname)
    {
        foreach (var label in hostname.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }
}
