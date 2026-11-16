using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace NobodyValley;

/// <summary>
/// Newline-delimited JSON over TCP (127.0.0.1 only).
///
/// Requests:  {"id": 1, "cmd": "observe", "agent": "Abigail", ...}
/// Responses: {"id": 1, "ok": true, "data": {...}}  |  {"id": 1, "ok": false, "error": "..."}
/// Events:    {"event": "arrived", "agent": "Abigail", ...}   (pushed to every client)
///
/// Sockets are serviced on background threads; requests are queued and handled on
/// the game's main thread (see <see cref="Drain"/>), since game state isn't thread-safe.
/// </summary>
internal sealed class BridgeServer
{
    private readonly IMonitor monitor;
    private readonly ConcurrentQueue<(Client client, JObject msg)> inbox = new();
    private readonly List<Client> clients = new();
    private TcpListener? listener;

    public int Port { get; private set; }

    public BridgeServer(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    /// <summary>Bind the first free port in [basePort, basePort + range), so several game instances can coexist.</summary>
    public void Start(int basePort, int range)
    {
        for (int port = basePort; port < basePort + range; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                listener = l;
                Port = port;
                break;
            }
            catch (SocketException) { }
        }
        if (listener == null)
        {
            monitor.Log($"No free port in {basePort}..{basePort + range - 1}; agent bridge disabled.", LogLevel.Error);
            return;
        }
        monitor.Log($"Agent bridge listening on 127.0.0.1:{Port}", LogLevel.Info);
        new Thread(AcceptLoop) { IsBackground = true, Name = "NobodyValley.Accept" }.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = listener!.AcceptTcpClient(); }
            catch (Exception ex) { monitor.Log($"Accept failed: {ex.Message}", LogLevel.Error); return; }

            var client = new Client(tcp);
            lock (clients) clients.Add(client);
            monitor.Log("Agent orchestrator connected.", LogLevel.Info);
            new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "NobodyValley.Client" }.Start();
        }
    }

    private void ReadLoop(Client client)
    {
        try
        {
            using var reader = new StreamReader(client.Tcp.GetStream(), new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try { inbox.Enqueue((client, JObject.Parse(line))); }
                catch (JsonException ex) { client.Send(new JObject { ["ok"] = false, ["error"] = $"bad json: {ex.Message}" }); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            lock (clients) clients.Remove(client);
            client.Tcp.Dispose();
            monitor.Log("Agent orchestrator disconnected.", LogLevel.Info);
        }
    }

    /// <summary>Handle queued requests. Call from the main thread only.</summary>
    public void Drain(Func<JObject, JToken?> handler)
    {
        while (inbox.TryDequeue(out var item))
        {
            var (client, msg) = item;
            var reply = new JObject { ["id"] = msg["id"] };
            try
            {
                reply["data"] = handler(msg);
                reply["ok"] = true;
            }
            catch (CommandException ex)
            {
                reply["ok"] = false;
                reply["error"] = ex.Message;
            }
            catch (Exception ex)
            {
                monitor.Log($"Command {msg["cmd"]} failed: {ex}", LogLevel.Error);
                reply["ok"] = false;
                reply["error"] = $"internal error: {ex.Message}";
            }
            client.Send(reply);
        }
    }

    public void Broadcast(string evt, JObject? payload = null)
    {
        var msg = payload ?? new JObject();
        msg["event"] = evt;
        Client[] snapshot;
        lock (clients) snapshot = clients.ToArray();
        foreach (var c in snapshot)
            c.Send(msg);
    }

    internal sealed class Client
    {
        public readonly TcpClient Tcp;
        private readonly object writeLock = new();

        public Client(TcpClient tcp) => Tcp = tcp;

        public void Send(JObject msg)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToString(Formatting.None) + "\n");
            try
            {
                lock (writeLock)
                    Tcp.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}

/// <summary>An expected failure that is reported back to the agent (e.g. "no such place").</summary>
internal sealed class CommandException : Exception
{
    public CommandException(string message) : base(message) { }
}
