using System;
using System.Collections.Generic;

namespace ScreenStocksBridge
{
    internal sealed class MainThreadQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<QueuedRequest> _queue = new Queue<QueuedRequest>();
        private readonly int _capacity;

        internal MainThreadQueue(int capacity) { _capacity = capacity; }

        internal bool Enqueue(BridgeRequest request, BridgeConnection connection)
        {
            lock (_gate)
            {
                if (_queue.Count >= _capacity) return false;
                _queue.Enqueue(new QueuedRequest(request, connection));
                return true;
            }
        }

        internal int Drain(Action<BridgeRequest, BridgeConnection> handler, int maximum)
        {
            var count = 0;
            while (count < maximum)
            {
                QueuedRequest item;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    item = _queue.Dequeue();
                }
                if (!item.Connection.IsClosed) handler(item.Request, item.Connection);
                count++;
            }
            return count;
        }

        private sealed class QueuedRequest
        {
            internal readonly BridgeRequest Request;
            internal readonly BridgeConnection Connection;
            internal QueuedRequest(BridgeRequest request, BridgeConnection connection) { Request = request; Connection = connection; }
        }
    }
}
