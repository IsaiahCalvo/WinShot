using System.IO;
using System.IO.Pipes;

namespace WinShot.Core;

/// <summary>
/// Single-instance command routing over the "WinShot.Commands" named pipe.
/// A second WinShot process (e.g. launched via winshot://) forwards its command with
/// <see cref="TrySendToRunningInstance"/> and exits; the primary instance runs a
/// <see cref="CommandServer"/> that raises <see cref="CommandReceived"/> for each line.
/// The event fires on the accept-loop thread — callers marshal to the UI thread.
/// </summary>
public sealed class CommandServer : IDisposable
{
    public const string PipeName = "WinShot.Commands";

    private static readonly HashSet<string> ValidCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "capture-area",
        "capture-fullscreen",
        "capture-previous",
        "all-in-one",
        "record",
        "ocr",
        "scrolling",
        "history",
        "settings",
        "self-timer",
        "restore-last",
    };

    private CancellationTokenSource? _cts;

    public event Action<string>? CommandReceived;

    /// <summary>Starts the background accept loop. Safe to call once; later calls are no-ops.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public void Dispose()
    {
        if (_cts is null) return;
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch
        {
            // Shutdown must never throw.
        }
        _cts = null;
    }

    /// <summary>
    /// Tries to hand a command to an already-running WinShot instance.
    /// Returns false when no instance is listening (500 ms connect timeout).
    /// </summary>
    public static bool TrySendToRunningInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            client.WaitForPipeDrain();
            return true;
        }
        catch (TimeoutException)
        {
            return false; // no running instance — the normal first-launch path
        }
        catch (Exception ex)
        {
            Log.Error("Failed to send command to running WinShot instance", ex);
            return false;
        }
    }

    /// <summary>
    /// Maps "winshot://capture-area", "winshot://capture-area/" or bare "capture-area"
    /// to the canonical command name; unknown input returns null.
    /// </summary>
    public static string? ParseCommand(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;

        string s = arg.Trim().Trim('"');
        if (s.StartsWith("winshot://", StringComparison.OrdinalIgnoreCase))
            s = s["winshot://".Length..];
        else if (s.StartsWith("winshot:", StringComparison.OrdinalIgnoreCase))
            s = s["winshot:".Length..];
        s = s.Trim('/');

        return ValidCommands.TryGetValue(s, out string? command) ? command : null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        CommandReceived?.Invoke(line.Trim());
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Command handler threw", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Command pipe accept loop error", ex);
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false); // avoid a tight failure loop
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
