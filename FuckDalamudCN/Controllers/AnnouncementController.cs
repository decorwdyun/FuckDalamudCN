using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

public class AnnouncementController: IDisposable
{
    private readonly ILogger<AnnouncementController> _logger;
    private readonly IClientState _clientState;
    private readonly IChatGui _chatGui;
    private const string Domain = "fuckotter.xuolu.com";
    private byte[] Key = Convert.FromBase64String("A4jMd8LZPoCH3+7lQdgYAJkfuu7AeCTS6ylsOcewtx8=");
    private byte[] IV  = Convert.FromBase64String("S9tHn0xZNiyPAK1Z6LxSFw=="); 
    private readonly LookupClient _dnsClient;
    private readonly CancellationTokenSource _cts = new();
    
    private string _announcement = "";

    public AnnouncementController(
        ILogger<AnnouncementController> logger,
        IClientState clientState,
        IChatGui chatGui
    )
    {
        _logger = logger;
        _clientState = clientState;
        _chatGui = chatGui;
        _dnsClient = new LookupClient();
        _clientState.Login += OnLogin;
        Task.Run(async () => await CheckAnnouncementAsync(), _cts.Token);
    }

    private void OnLogin()
    {
        Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => PrintAnnouncement());
    }
    
    private async Task CheckAnnouncementAsync()
    {
        try
        {
            var encryptedMessage = await GetEncryptedMessageFromDnsTxtAsync();
            if (string.IsNullOrEmpty(encryptedMessage)) return;
            var decryptedMessage = Decrypt(encryptedMessage, Key, IV);
            if (string.IsNullOrEmpty(decryptedMessage)) return;

            _announcement = decryptedMessage;
            if (_clientState.IsLoggedIn)
            {
                PrintAnnouncement();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
    }

    private void PrintAnnouncement()
    {
        if (_announcement != "")
        {
            _chatGui.PrintError($"FuckDalamudCN 公告: {_announcement}");
        }
    }
    
    private async Task<string> GetEncryptedMessageFromDnsTxtAsync()
    {
        var queryResult = await _dnsClient.QueryAsync(Domain, QueryType.TXT, cancellationToken: _cts.Token);
        var txtRecord = queryResult.Answers
            .OfType<DnsClient.Protocol.TxtRecord>()
            .FirstOrDefault();
    
        if (txtRecord == null)
        {
            return string.Empty;
        }

        var fullText = string.Join("", txtRecord.Text);
        return fullText.Trim();
    }
    
    private string Decrypt(string cipherText, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

        using var ms = new MemoryStream(Convert.FromBase64String(cipherText));
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var reader = new StreamReader(cs);
        return reader.ReadToEnd();
    }

    private static string Encrypt(string plainText, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
    
        var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
    
        using var ms = new MemoryStream();
        using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
        using (var writer = new StreamWriter(cs, Encoding.UTF8))
        {
            writer.Write(plainText);
        }
        return Convert.ToBase64String(ms.ToArray());
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _clientState.Login -= OnLogin;
    }
}