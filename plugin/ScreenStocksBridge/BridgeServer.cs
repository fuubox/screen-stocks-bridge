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
        internal const int MaxFrameBytes = 16 * 1024;
        private readonly int _port;
        private readonly string _token;
        private readonly MainThreadQueue _requests = new MainThreadQueue(128);
        private readonly object _gate = new object();
        private readonly List<BridgeConnection> _clients = new List<BridgeConnection>();
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _stopping;

        internal BridgeServer(int port, string token) { _port = port; _token = token; }

        internal void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start(4);
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ScreenStocksBridge.Accept" };
            _acceptThread.Start();
        }

        internal int Drain(Action<BridgeRequest, BridgeConnection> handler, int maximum) => _requests.Drain(handler, maximum);
        internal bool HasMarketSubscribers
        {
            get { lock (_gate) { foreach (var client in _clients) if (!client.IsClosed && client.IsSubscribed) return true; } return false; }
        }
        internal bool HasHumanActivitySubscribers
        {
            get { lock (_gate) { foreach (var client in _clients) if (!client.IsClosed && client.HasHumanActivitySubscriptions) return true; } return false; }
        }
        internal bool HasNewsSubscribers
        {
            get { lock (_gate) { foreach (var client in _clients) if (!client.IsClosed && client.IsNewsSubscribed) return true; } return false; }
        }

        internal List<string> GetSubscribedHumanActivityStockIds()
        {
            var stockIds = new HashSet<string>(StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (var client in _clients)
                    if (!client.IsClosed) client.AddHumanActivitySubscriptionIds(stockIds);
            }
            return new List<string>(stockIds);
        }

        internal bool HasHumanActivitySubscriber(string stockId)
        {
            lock (_gate)
            {
                foreach (var client in _clients)
                    if (!client.IsClosed && client.IsSubscribedToHumanActivity(stockId)) return true;
            }
            return false;
        }

        internal void Publish(string eventName, string rawData)
        {
            var frame = ProtocolJson.Event(eventName, rawData);
            if (Encoding.UTF8.GetByteCount(frame) > MaxFrameBytes) return;
            BridgeConnection[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients) if (client.IsSubscribed && !client.IsClosed) client.Send(frame, true);
        }

        internal void PublishHumanActivity(string stockId, string rawData)
        {
            var frame = ProtocolJson.Event("human_activity.updated", rawData);
            if (Encoding.UTF8.GetByteCount(frame) > MaxFrameBytes) return;
            BridgeConnection[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients)
                if (!client.IsClosed && client.IsSubscribedToHumanActivity(stockId)) client.Send(frame, true);
        }

        internal void PublishHumanActivityTo(BridgeConnection connection, string rawData)
        {
            var frame = ProtocolJson.Event("human_activity.updated", rawData);
            if (Encoding.UTF8.GetByteCount(frame) <= MaxFrameBytes && !connection.IsClosed)
                connection.Send(frame, true);
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
                    if (_clients.Count >= 4) { tcp.Close(); continue; }
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
            catch (InvalidDataException) { connection.Send(ProtocolJson.Error(string.Empty, "frame_too_large", "Request exceeds the 16 KiB frame limit."), false); }
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
        private readonly HashSet<string> _humanActivitySubscriptions = new HashSet<string>(StringComparer.Ordinal);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _closed;
        private Thread? _writer;
        private Thread? _reader;
        internal Stream Stream { get; }
        internal bool IsClosed => _closed;
        internal bool IsSubscribed { get; set; }
        internal bool IsNewsSubscribed { get; set; }
        internal bool HasHumanActivitySubscriptions => _humanActivitySubscriptions.Count > 0;

        internal void SubscribeHumanActivity(string stockId) { _humanActivitySubscriptions.Add(stockId); }
        internal void UnsubscribeHumanActivity(string stockId) { _humanActivitySubscriptions.Remove(stockId); }
        internal bool IsSubscribedToHumanActivity(string stockId) { return _humanActivitySubscriptions.Contains(stockId); }
        internal void AddHumanActivitySubscriptionIds(HashSet<string> destination)
        {
            foreach (var stockId in _humanActivitySubscriptions) destination.Add(stockId);
        }

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

        internal void Send(string json, bool isEvent)
        {
            if (_closed) return;
            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > BridgeServer.MaxFrameBytes) { Close(); return; }
            lock (_sendGate)
            {
                if (_outbound.Count >= 64)
                {
                    if (isEvent)
                    {
                        var copy = new Queue<OutboundFrame>();
                        var removed = false;
                        while (_outbound.Count > 0)
                        {
                            var frame = _outbound.Dequeue();
                            if (!removed && frame.IsEvent) { removed = true; continue; }
                            copy.Enqueue(frame);
                        }
                        while (copy.Count > 0) _outbound.Enqueue(copy.Dequeue());
                        if (!removed) return;
                    }
                    else { Close(); return; }
                }
                _outbound.Enqueue(new OutboundFrame(json, isEvent));
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
                    lock (_sendGate) if (_outbound.Count > 0) frame = _outbound.Dequeue();
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

        private sealed class OutboundFrame
        {
            internal readonly string Json;
            internal readonly bool IsEvent;
            internal OutboundFrame(string json, bool isEvent) { Json = json; IsEvent = isEvent; }
        }
    }
}
