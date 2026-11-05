using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class BridgeServer : IDisposable
    {
        internal const int MaxFrameBytes = 128 * 1024;
        internal const int DefaultMaxClientConnections = 4;
        internal const int MinMaxClientConnections = 1;
        internal const int MaxMaxClientConnections = 256;
        private readonly int _port;
        private readonly string _token;
        private readonly int _maxClientConnections;
        private readonly MainThreadQueue _requests = new MainThreadQueue(128);
        private readonly object _gate = new object();
        private readonly List<BridgeConnection> _clients = new List<BridgeConnection>();
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _stopping;

        internal BridgeServer(int port, string token, int maxClientConnections)
        {
            _port = port;
            _token = token;
            _maxClientConnections = Math.Max(MinMaxClientConnections,
                Math.Min(MaxMaxClientConnections, maxClientConnections));
        }

        internal void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start(_maxClientConnections);
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ScreenStocksBridge.Accept" };
            _acceptThread.Start();
        }

        internal int Drain(Action<BridgeRequest, BridgeConnection> handler, int maximum) => _requests.Drain(handler, maximum);
        internal BridgeConnection[] Connections { get { lock (_gate) return _clients.ToArray(); } }
        internal bool HasMarketSubscribers
        {
            get { lock (_gate) { foreach (var client in _clients) if (!client.IsClosed && client.IsSubscribed) return true; } return false; }
        }
        internal bool HasNewsSubscribers
        {
            get { lock (_gate) { foreach (var client in _clients) if (!client.IsClosed && client.IsNewsSubscribed) return true; } return false; }
        }
        internal bool HasAutoActionToastSubscribers
        {
            get { lock (_gate) { foreach (var client in _clients) if (!client.IsClosed && client.IsAutoActionToastSubscribed) return true; } return false; }
        }

        internal void Publish(string eventName, string rawData)
        {
            BridgeConnection[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients) if (client.IsSubscribed && !client.IsClosed) client.SendStream(eventName, rawData);
        }

        internal void PublishNewsTicker(string rawData)
        {
            var frame = ProtocolJson.Event("news.updated", rawData);
            if (Encoding.UTF8.GetByteCount(frame) > MaxFrameBytes) return;
            BridgeConnection[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients)
                if (!client.IsClosed && client.IsNewsSubscribed) client.Send(frame, true);
        }

        internal void PublishAutoActionToast(string rawData)
        {
            var frame = ProtocolJson.Event("auto_action.toast", rawData);
            if (Encoding.UTF8.GetByteCount(frame) > MaxFrameBytes) return;
            BridgeConnection[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients)
                if (!client.IsClosed && client.IsAutoActionToastSubscribed) client.Send(frame, true);
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient? tcp = null;
                try { tcp = _listener!.AcceptTcpClient(); }
                catch (SocketException) { if (!_stopping) Thread.Sleep(50); continue; }
                catch (ObjectDisposedException) { break; }

                lock (_gate)
                {
                    PruneClosed();
                    if (_clients.Count >= _maxClientConnections) { tcp.Close(); continue; }
                    var connection = new BridgeConnection(tcp, RemoveClient);
                    _clients.Add(connection);
                    connection.Start(ReadLoop, ClientEnded);
                }
            }
        }

        private void ReadLoop(BridgeConnection connection)
        {
            try
            {
                while (!_stopping && !connection.IsClosed)
                {
                    var line = ReadFrame(connection.Stream);
                    if (line == null) break;
                    BridgeRequest? request;
                    try
                    {
                        request = JsonUtility.FromJson<BridgeRequest>(line);
                        if (request != null)
                        {
                            var paramsJson = JsonObjectParser.ExtractTopLevelObject(line, "params");
                            request.@params = paramsJson == null ? null : JsonUtility.FromJson<RequestParams>(paramsJson);
                        }
                    }
                    catch { connection.Send(ProtocolJson.Error(string.Empty, "malformed_json", "Request must be valid JSON."), false); break; }
                    if (request == null || string.IsNullOrWhiteSpace(request.id) || string.IsNullOrWhiteSpace(request.method))
                    {
                        connection.Send(ProtocolJson.Error(request?.id ?? string.Empty, "invalid_request", "Request requires non-empty id and method."), false);
                        continue;
                    }
                    if (!FixedTimeEquals(request.token, _token))
                    {
                        connection.Send(ProtocolJson.Error(request.id, "unauthorized", "Token is missing or invalid."), false);
                        break;
                    }
                    connection.MarkAuthenticated();
                    if (!_requests.Enqueue(request, connection))
                    {
                        connection.Send(ProtocolJson.Error(request.id, "busy", "Main-thread request queue is full."), false);
                    }
                }
            }
            catch (InvalidDataException) { connection.Send(ProtocolJson.Error(string.Empty, "frame_too_large", "Request exceeds the 128 KiB frame limit."), false); }
            catch (IOException) { }
            catch (SocketException) { }
            finally { connection.Close(); }
        }

        private static string? ReadFrame(Stream stream)
        {
            var bytes = new List<byte>(256);
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
                if (value == 10) return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                if (bytes.Count >= MaxFrameBytes) throw new InvalidDataException();
                bytes.Add((byte)value);
            }
        }

        private static bool FixedTimeEquals(string? a, string b)
        {
            if (a == null) return false;
            var left = Encoding.UTF8.GetBytes(a);
            var right = Encoding.UTF8.GetBytes(b);
            var diff = left.Length ^ right.Length;
            var count = Math.Max(left.Length, right.Length);
            for (var i = 0; i < count; i++) diff |= (i < left.Length ? left[i] : 0) ^ (i < right.Length ? right[i] : 0);
            return diff == 0;
        }

        private void ClientEnded(BridgeConnection connection) { lock (_gate) _clients.Remove(connection); }
        private void RemoveClient(BridgeConnection connection) { ClientEnded(connection); }
        private void PruneClosed() { _clients.RemoveAll(c => c.IsClosed); }

        public void Dispose()
        {
            _stopping = true;
            _listener?.Stop();
            BridgeConnection[] clients;
            lock (_gate) { clients = _clients.ToArray(); _clients.Clear(); }
            foreach (var client in clients) client.Close();
        }

    }

    internal sealed class BridgeConnection
    {
        private readonly TcpClient _tcp;
        private readonly Action<BridgeConnection> _onClosed;
        private readonly object _sendGate = new object();
        private readonly Queue<OutboundFrame> _outbound = new Queue<OutboundFrame>();
        private readonly Dictionary<string, long> _streamSequences = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _streamDrops = new Dictionary<string, long>(StringComparer.Ordinal);
        private bool _lossPending;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _closed;
        private Thread? _writer;
        private Thread? _reader;
        internal Stream Stream { get; }
        internal bool IsClosed => _closed;
        internal bool IsSubscribed { get; set; }
        internal string SessionId { get; } = Guid.NewGuid().ToString("N");
        internal bool IsActivitySubscribed { get; set; }
        internal bool IsCandleSubscribed { get; set; }
        internal string? RawLayoutRevision { get; set; }
        internal bool IsNewsSubscribed { get; set; }
        internal bool IsAutoActionToastSubscribed { get; set; }
        internal BridgeConnection(TcpClient tcp, Action<BridgeConnection> onClosed)
        {
            _tcp = tcp; _onClosed = onClosed; _tcp.NoDelay = true; _tcp.ReceiveTimeout = 5000; _tcp.SendTimeout = 1000;
            Stream = tcp.GetStream();
        }

        internal void Start(Action<BridgeConnection> read, Action<BridgeConnection> ended)
        {
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ScreenStocksBridge.Writer" };
            _reader = new Thread(() => { try { read(this); } finally { ended(this); } }) { IsBackground = true, Name = "ScreenStocksBridge.Reader" };
            _writer.Start(); _reader.Start();
        }

        internal void MarkAuthenticated() { _tcp.ReceiveTimeout = 0; }

        internal void SendActivity(string rawData) => Send(ProtocolJson.Event("activity.changed", rawData), true);
        internal void SendStream(string name, string rawData) => Send(ProtocolJson.Event(name, rawData), true, name);

        internal void Send(string json, bool isEvent, string? streamName = null)
        {
            if (_closed) return;
            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes + (streamName == null ? 0 : 512) > BridgeServer.MaxFrameBytes) { Close(); return; }
            lock (_sendGate)
            {
                var sequence = 0L;
                if (streamName != null)
                {
                    _streamSequences.TryGetValue(streamName, out sequence);
                    _streamSequences[streamName] = ++sequence;
                }
                if (_outbound.Count >= 64)
                {
                    // A dropped activity chunk invalidates the replica even if the
                    // feed stays quiet afterward. Close rather than lose continuity.
                    if (IsActivitySubscribed) { Close(); return; }
                    if (isEvent)
                    {
                        var copy = new Queue<OutboundFrame>();
                        var removed = false;
                        while (_outbound.Count > 0)
                        {
                            var frame = _outbound.Dequeue();
                            if (!removed && frame.IsEvent) { removed = true; RecordDrop(frame.StreamName); continue; }
                            copy.Enqueue(frame);
                        }
                        while (copy.Count > 0) _outbound.Enqueue(copy.Dequeue());
                        if (!removed) { RecordDrop(streamName); _wake.Set(); return; }
                    }
                    else { Close(); return; }
                }
                _outbound.Enqueue(new OutboundFrame(json, isEvent, streamName, sequence));
            }
            _wake.Set();
        }

        private void WriteLoop()
        {
            try
            {
                while (!_closed)
                {
                    OutboundFrame? frame = null;
                    string? loss = null;
                    lock (_sendGate)
                    {
                        if (_lossPending) { loss = LossJson(); _lossPending = false; }
                        else if (_outbound.Count > 0) frame = _outbound.Dequeue();
                        if (frame?.StreamName != null)
                        {
                            _streamDrops.TryGetValue(frame.StreamName, out var drops);
                            frame = new OutboundFrame(frame.Json.Substring(0, frame.Json.Length - 1) +
                                ",\"delivery\":{\"generation\":" + BridgeJson.Quote(SessionId) + ",\"stream\":" + BridgeJson.Quote(frame.StreamName) +
                                ",\"sequence\":" + frame.Sequence + ",\"droppedEvents\":" + drops + "}}", true);
                        }
                    }
                    if (loss != null)
                    {
                        var lossBytes = Encoding.UTF8.GetBytes(loss + "\n");
                        Stream.Write(lossBytes, 0, lossBytes.Length); Stream.Flush(); continue;
                    }
                    if (frame == null) { _wake.WaitOne(250); continue; }
                    var bytes = Encoding.UTF8.GetBytes(frame.Json + "\n");
                    Stream.Write(bytes, 0, bytes.Length);
                    Stream.Flush();
                }
            }
            catch { Close(); }
        }

        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _tcp.Close(); } catch { }
            _wake.Set();
            _onClosed(this);
        }

        private void RecordDrop(string? stream)
        {
            stream = stream ?? "other";
            _streamDrops.TryGetValue(stream, out var count);
            _streamDrops[stream] = count + 1;
            _lossPending = true;
        }

        private string LossJson()
        {
            var items = new List<string>();
            foreach (var pair in _streamDrops) items.Add(BridgeJson.Quote(pair.Key) + ":" + pair.Value);
            return ProtocolJson.Event("stream.loss", "{\"origin\":\"bridge\",\"generation\":" + BridgeJson.Quote(SessionId) + ",\"droppedEvents\":{" + string.Join(",", items) + "}}");
        }

        private sealed class OutboundFrame
        {
            internal readonly string Json;
            internal readonly bool IsEvent;
            internal readonly string? StreamName;
            internal readonly long Sequence;
            internal OutboundFrame(string json, bool isEvent, string? streamName = null, long sequence = 0)
            { Json = json; IsEvent = isEvent; StreamName = streamName; Sequence = sequence; }
        }
    }
}
