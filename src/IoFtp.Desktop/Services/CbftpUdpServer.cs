using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

namespace IoFtp.Desktop.Services;

/// <summary>Minimal cbftp UDP command bridge used by mIRC autotrader scripts.</summary>
internal sealed class CbftpUdpServer : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly string _password;
    private readonly Func<RawRequest, Task<IReadOnlyList<RawApiResult>>> _raw;
    private readonly Func<ApiTransferRequest, Task<object>> _transfer;
    private readonly Func<ApiDownloadRequest, Task<object>> _download;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    public CbftpUdpServer(IPAddress address, int port, string password,
        Func<RawRequest, Task<IReadOnlyList<RawApiResult>>> raw,
        Func<ApiTransferRequest, Task<object>> transfer,
        Func<ApiDownloadRequest, Task<object>> download)
    {
        _password = password; _raw = raw; _transfer = transfer; _download = download;
        _udp = new UdpClient(new IPEndPoint(address, port));
    }

    public Task StartAsync()
    {
        _listener = ListenAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(packet);
        }
    }

    private async Task HandleAsync(UdpReceiveResult packet)
    {
        string reply;
        try
        {
            var text = Encoding.UTF8.GetString(packet.Buffer).Trim();
            var separator = text.IndexOf(' ');
            if (separator <= 0 || !ConstantTimeEquals(text[..separator], _password)) throw new UnauthorizedAccessException("invalid password");
            var commandLine = text[(separator + 1)..].Trim();
            var verbEnd = commandLine.IndexOf(' ');
            var verb = (verbEnd < 0 ? commandLine : commandLine[..verbEnd]).ToLowerInvariant();
            var arguments = verbEnd < 0 ? "" : commandLine[(verbEnd + 1)..].Trim();
            reply = verb switch
            {
                "raw" => await RunRawAsync(arguments),
                "fxp" => await RunFxpAsync(arguments),
                "race" => await RunRaceAsync(arguments),
                "download" => await RunDownloadAsync(arguments),
                _ => throw new ArgumentException($"unsupported command: {verb}")
            };
        }
        catch (Exception exception) { reply = $"ERROR {exception.Message}"; }
        try { await _udp.SendAsync(Encoding.UTF8.GetBytes(reply), packet.RemoteEndPoint); } catch { }
    }

    private async Task<string> RunRawAsync(string arguments)
    {
        var split = arguments.IndexOf(' '); if (split <= 0) throw new ArgumentException("raw requires sites and command");
        var sites = arguments[..split].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = await _raw(new(arguments[(split + 1)..], sites));
        return string.Join("\n", results.Select(result =>
            result.Error is null ? $"{result.Name} {result.Result}" : $"{result.Name} ERROR {result.Error}"));
    }

    private async Task<string> RunFxpAsync(string arguments)
    {
        var fields = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 4) throw new ArgumentException("fxp requires source-site source-path target-site target-path");
        string sourceSite; string sourcePath; string targetSite; string targetPath; string name;
        if (fields.Length >= 5)
        {
            // Standard cbftp form:
            // fxp <srcsite> <srcpath> <srcfile> <dstsite> <dstpath> [dstfile]
            sourceSite = fields[0]; sourcePath = fields[1]; name = fields[2];
            targetSite = fields[3]; targetPath = fields[4];
        }
        else
        {
            // Compact form also used by d-tool:
            // fxp <srcsite> <full-src-path> <dstsite> <full-dst-path>
            sourceSite = fields[0]; sourcePath = Parent(fields[1]);
            targetSite = fields[2]; targetPath = Parent(fields[3]);
            var sourceName = Path.GetFileName(fields[1].TrimEnd('/'));
            var targetName = Path.GetFileName(fields[3].TrimEnd('/'));
            name = sourceName.Length > 0 ? sourceName : targetName;
        }
        await _transfer(new(sourceSite, sourcePath, null, targetSite, targetPath, null, name));
        return $"OK FXP {name}";
    }

    private async Task<string> RunRaceAsync(string arguments)
    {
        var fields = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 3) throw new ArgumentException("race requires section release source,target...");
        var sites = fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sites.Length < 2) throw new ArgumentException("race requires at least one source and target");
        foreach (var target in sites.Skip(1)) await _transfer(new(sites[0], null, fields[0], target, null, fields[0], fields[1]));
        return $"OK RACE {fields[1]} {sites.Length - 1} target(s)";
    }

    private async Task<string> RunDownloadAsync(string arguments)
    {
        var split = arguments.IndexOf(' '); if (split <= 0) throw new ArgumentException("download requires site and path");
        await _download(new(arguments[..split], arguments[(split + 1)..]));
        return "OK DOWNLOAD";
    }

    private static string Parent(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "/" : normalized[..separator];
    }

    private static bool ConstantTimeEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel(); _udp.Dispose();
        if (_listener is not null) try { await _listener; } catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }
}
