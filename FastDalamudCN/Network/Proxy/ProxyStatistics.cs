using System.Text.Json;
using Dalamud.Plugin;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Proxy;

internal sealed class ProxyStatistics : IDisposable
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<ProxyStatistics> _logger;
    private readonly string _filePath;
    private readonly Timer _saveTimer;
    private readonly object _saveLock = new();

    private long _acceleratedCount;
    private long _downloadedBytes;
    private Totals _lastSaved;
    private bool _disposed;

    public ProxyStatistics(IDalamudPluginInterface pluginInterface, ILogger<ProxyStatistics> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "proxy-statistics.json");
        _lastSaved = LoadTotals();
        _acceleratedCount = _lastSaved.AcceleratedCount;
        _downloadedBytes = _lastSaved.DownloadedBytes;
        _saveTimer = new Timer(_ => SaveTotals(), null, SaveInterval, SaveInterval);
    }

    public Totals Current => new(Interlocked.Read(ref _acceleratedCount), Interlocked.Read(ref _downloadedBytes));

    public async Task<HttpResponseMessage> TrackResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            var originalContent = response.Content;
            var stream = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var trackedContent = new StreamContent(new ProxyResponseStream(stream, originalContent, this));
            foreach (var header in originalContent.Headers)
            {
                trackedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = trackedContent;
            Interlocked.Increment(ref _acceleratedCount);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public void RecordDownloadedBytes(int bytesRead)
    {
        if (bytesRead > 0)
        {
            Interlocked.Add(ref _downloadedBytes, bytesRead);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveTotals(disposing: true);
    }

    private Totals LoadTotals()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var totals = JsonSerializer.Deserialize<Totals>(File.ReadAllText(_filePath));
                return new Totals(Math.Max(0, totals.AcceleratedCount), Math.Max(0, totals.DownloadedBytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "读取代理统计失败");
        }

        return default;
    }

    private void SaveTotals(bool disposing = false)
    {
        lock (_saveLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = disposing;
            var totals = Current;
            if (totals == _lastSaved)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temporaryPath = _filePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(totals));
                File.Move(temporaryPath, _filePath, overwrite: true);
                _lastSaved = totals;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "保存代理统计失败");
            }
        }
    }

    public readonly record struct Totals(long AcceleratedCount, long DownloadedBytes);
}
