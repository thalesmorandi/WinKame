using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WinKame.Interop;

namespace WinKame.Ipc;

/// <summary>Keeps one WinKame per user; later launches hand their arguments to the first one.</summary>
internal static class SingleInstance
{
    // string.GetHashCode is randomized per process, so build a stable name from the user name.
    private static readonly string Id = "WinKame-" + new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private static Mutex? _mutex;

    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, @"Local\" + Id, out var created);
        return created;
    }

    public static bool SendToPrimary(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            pipe.Connect(3000);
            // We were started by the user's click, so we may give the primary the right to come to front.
            if (Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
                Native.AllowSetForegroundWindow((int)pid);
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Listen(Action<string[]> onArgs, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, ct).ConfigureAwait(false);
                    var args = JsonSerializer.Deserialize<string[]>(ms.ToArray()) ?? [];
                    Ui.OnUi(() => onArgs(args));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, ct);
    }
}
