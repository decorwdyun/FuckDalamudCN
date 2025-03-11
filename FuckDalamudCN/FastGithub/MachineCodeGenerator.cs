namespace FuckDalamudCN.FastGithub;

internal sealed class MachineCodeGenerator
{
    private static readonly Lazy<MachineCodeGenerator> _instance = new(() => new MachineCodeGenerator());

    private MachineCodeGenerator()
    {
        MachineCode = $"{GenerateRandomHexString(32)}:{GenerateRandomHexString(32)}:{GenerateRandomHexString(32)}".ToUpperInvariant();
    }

    public static MachineCodeGenerator Instance => _instance.Value;
    
    public string MachineCode { get; }

    private string GenerateRandomHexString(int length)
    {
        var random = new Random();
        var buffer = new byte[length / 2];
        random.NextBytes(buffer);
        return BitConverter.ToString(buffer).Replace("-", "").ToLower();
    }
}