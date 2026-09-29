using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CDisplayEx.CSharp;

/// <summary>
/// Owns one Fast Reader/Viewer profile process per signed-in Windows user.
/// Later launches forward their already-normalized open request to that process.
/// PDFium worker processes bypass this coordinator in Program.Main.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _primaryMutex;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;
    private bool _ownsMutex;

    private static readonly string IdentityHash = CreateIdentityHash();
    private static readonly string MutexName =
        $"Local\\FastReaderViewer.Profile.{IdentityHash}";
    private static readonly string PipeName =
        $"FastReaderViewer.Profile.{IdentityHash}";

    public bool IsPrimary { get; }

    public SingleInstanceCoordinator()
    {
        _primaryMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var primary = createdNew;
        if (!primary)
        {
            try { primary = _primaryMutex.WaitOne(0); }
            catch (AbandonedMutexException) { primary = true; }
        }
        IsPrimary = primary;
        _ownsMutex = primary;
    }

    public void StartServer(Action<CommandLineOptions.OpenRequest> requestReceived)
    {
        if (!IsPrimary || _serverTask is not null) return;
        _serverTask = Task.Run(() => ServerLoopAsync(requestReceived, _cancellation.Token));
    }

    public async Task<bool> ForwardAsync(CommandLineOptions.OpenRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsPrimary) return false;
        var payload = JsonSerializer.Serialize(request);
        var deadline = Environment.TickCount64 + 7000;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                attempt.CancelAfter(600);
                await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                await using var writer = new StreamWriter(client, new UTF8Encoding(false),
                    4096, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task ServerLoopAsync(
        Action<CommandLineOptions.OpenRequest> requestReceived,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var request = JsonSerializer.Deserialize<CommandLineOptions.OpenRequest>(line);
                requestReceived(request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ExtendedDiagnostics.LogException("Single-instance command server failed", exception);
                try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static string CreateIdentityHash()
    {
        string user;
        try
        {
            user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch { user = Environment.UserName; }
        var identity = user + "\n" + Path.GetFullPath(UserSettings.ProfileDirectory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch { }
        _cancellation.Dispose();
        if (_ownsMutex)
        {
            try { _primaryMutex.ReleaseMutex(); }
            catch { }
            _ownsMutex = false;
        }
        _primaryMutex.Dispose();
    }
}
