using System.Security.Cryptography;

namespace FuckDalamudCN.Utils;

public static class IpGenerator
{
    public static string GenerateRandomIp()
    {
        var a = RandomNumberGenerator.GetInt32(1, 255);
        var b = RandomNumberGenerator.GetInt32(0, 256);
        var c = RandomNumberGenerator.GetInt32(0, 256);
        var d = RandomNumberGenerator.GetInt32(1, 255);

        return $"{a}.{b}.{c}.{d}";
    }
}