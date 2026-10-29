using System.Net;
using System.Net.Sockets;

namespace GolfJoyBridge;

/// <summary>
/// Lets GRS.dll talk to a Wi-Fi connected unit. The DLL is given "127.0.0.1" as its unit address, and
/// this relay stands in for the unit on that address:
///   - answers the DLL's one UDP "Get firmware ver" probe (the step that only works on the wired
///     connection; on Wi-Fi the real phone app never does this step, so the DLL gets stuck waiting
///     for a reply). The reply is the exact bytes captured from a real wired unit - confirmed working.
///   - forwards TCP 5288 (control/shot data) and 5277 (camera) straight through to the unit's real
///     Wi-Fi address, byte for byte, in both directions.
/// </summary>
public sealed class WirelessRelay : IDisposable
{
    private static readonly int[] TcpPorts = { 5288, 5277 };
    private const int UdpProbePort = 8813;

    // Captured once from a real wired unit's reply to "Get firmware ver". The DLL does not appear to
    // check this beyond "looks like a valid reply", so replaying it works regardless of the unit's
    // actual firmware string.
    private static readonly byte[] FirmwareReply = BuildFirmwareReply();

    private static byte[] BuildFirmwareReply()
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes("Firmware ver:SPICA3_V2.0.3\r\nSupport tcp\r\n");
        var msg = new byte[5 + text.Length];
        msg[0] = 0xAA; msg[1] = 0x55; msg[2] = 0xEE;
        msg[3] = (byte)(text.Length & 0xFF);
        msg[4] = (byte)((text.Length >> 8) & 0xFF);
        Buffer.BlockCopy(text, 0, msg, 5, text.Length);
        return msg;
    }

    private readonly string _targetIp;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly List<Socket> _listeners = new();

    public WirelessRelay(string targetIp) => _targetIp = targetIp;

    public void Start()
    {
        var ct = _cts.Token;
        _tasks.Add(Task.Run(() => RunUdpResponder(ct), ct));
        foreach (int port in TcpPorts)
            _tasks.Add(Task.Run(() => RunTcpForwarder(port, ct), ct));
        Log.Info($"Wireless relay started, forwarding to {_targetIp} (ports {string.Join(", ", TcpPorts)}, UDP {UdpProbePort})");
    }

    // ---------- UDP: answer the DLL's one "Get firmware ver" probe ----------

    private async Task RunUdpResponder(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, UdpProbePort));

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (result.Buffer.Length >= 2 && result.Buffer[0] == 0xAA && result.Buffer[1] == 0x55)
                {
                    try { await udp.SendAsync(FirmwareReply, result.RemoteEndPoint, ct).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Info("Wireless relay: could not reply to the unit probe: " + ex.Message); }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Info($"Wireless relay: UDP responder on port {UdpProbePort} failed ({ex.GetType().Name}: {ex.Message}). " +
                          "Another program may already be using that port.");
        }
        finally { udp?.Dispose(); }
    }

    // ---------- TCP: forward straight through to the real unit ----------

    private async Task RunTcpForwarder(int port, CancellationToken ct)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            lock (_listeners) _listeners.Add(listener.Server);

            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                _ = HandleConnection(client, port, ct);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Info($"Wireless relay: could not listen on port {port} ({ex.GetType().Name}: {ex.Message}). " +
                          "Another program may already be using it.");
        }
        finally { try { listener?.Stop(); } catch { } }
    }

    private async Task HandleConnection(TcpClient client, int port, CancellationToken ct)
    {
        TcpClient? upstream = null;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(_targetIp, port, ct).ConfigureAwait(false);
            client.NoDelay = true;
            upstream.NoDelay = true;
            Log.Info($"Wireless relay: port {port} connected to the unit");

            var a = PumpAsync(client.GetStream(), upstream.GetStream(), ct);
            var b = PumpAsync(upstream.GetStream(), client.GetStream(), ct);
            await Task.WhenAny(a, b).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Wireless relay: port {port} could not reach the unit at {_targetIp} ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            try { client.Close(); } catch { }
            try { upstream?.Close(); } catch { }
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                int n = await from.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        catch { /* either side closing is normal */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_listeners) { foreach (var s in _listeners) { try { s.Close(); } catch { } } }
        try { Task.WhenAll(_tasks).Wait(2000); } catch { }
        _cts.Dispose();
    }
}
