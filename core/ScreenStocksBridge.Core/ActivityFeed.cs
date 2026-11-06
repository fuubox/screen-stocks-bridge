using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScreenStocksBridge
{
    // One consumer seam for ownership, immutable snapshots and ordered changes.
    // Native game hooks supply accepted state; no transport or Unity dependency lives here.
    internal sealed class ActivityFeed
    {
        internal const int SchemaVersion = 2;
        internal const int PageSize = 32;
        private readonly Func<double> _clock;
        private readonly Action<string> _setFocus;
        private double _lastPinAt = double.NegativeInfinity;
        private string _graphStock = string.Empty;
        private string _pinnedStock = string.Empty;
        internal string? Owner { get; private set; }
        internal string EffectiveStock => Owner == null ? _graphStock : _pinnedStock;
        internal string Mode => Owner == null ? "follow_graph" : "pinned";
        internal string Generation { get; private set; } = Guid.NewGuid().ToString("N");
        internal long Revision { get; private set; }
        private string _metadata = "{}";
        private Dictionary<string, string> _rows = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, SavedSnapshot> _snapshots = new Dictionary<string, SavedSnapshot>();
        internal event Action<IReadOnlyList<string>>? Changed;

        internal ActivityFeed(Func<double> clock, Action<string> setFocus)
        { _clock = clock; _setFocus = setFocus; }

        internal string RouteGraphFocus(string stock)
        { _graphStock = stock ?? string.Empty; return EffectiveStock; }

        internal void SetInitialGraph(string stock) { if (Owner == null) _graphStock = stock; }

        internal void Pin(string connection, string stock, bool takeover)
        {
            if (Owner != null && Owner != connection && !takeover)
                throw new ActivityError("ownership_conflict", "Another connection owns the pin; use activity.takeover explicitly.");
            if (Owner == connection && _pinnedStock == stock) return;
            var remaining = 5 - (_clock() - _lastPinAt);
            if (remaining > 0) throw new ActivityError("rate_limited", "Pin changes require five seconds between requests.", (int)Math.Ceiling(remaining * 1000));
            // Failure must leave ownership and cooldown unchanged.
            _setFocus(stock);
            Owner = connection; _pinnedStock = stock; _lastPinAt = _clock();
        }

        internal void Unpin(string connection)
        {
            if (Owner == null) return;
            if (Owner != connection) throw new ActivityError("ownership_conflict", "Only the current owner may unpin.");
            _setFocus(_graphStock);
            Owner = null; _pinnedStock = string.Empty;
        }

        internal void Disconnect(string connection)
        {
            _snapshots.Remove(connection);
            if (Owner != connection) return;
            // Authority ends even when the scene cannot currently accept a focus change.
            Owner = null; _pinnedStock = string.Empty;
            _setFocus(_graphStock);
        }

        internal void Reset()
        {
            Generation = Guid.NewGuid().ToString("N");
            Revision = 0; _metadata = "{}"; _rows.Clear(); _snapshots.Clear();
        }

        internal void Publish(string metadata, IDictionary<string, string> rows, bool rowsUnchanged = false)
        {
            if (rowsUnchanged && metadata == _metadata) return;
            var changes = new List<string>();
            if (!rowsUnchanged)
            {
                foreach (var pair in rows.OrderBy(p => p.Key, StringComparer.Ordinal))
                    if (!_rows.TryGetValue(pair.Key, out var prior) || prior != pair.Value)
                        changes.Add("{\"op\":\"upsert\",\"key\":" + BridgeJson.Quote(pair.Key) + ",\"value\":" + pair.Value + "}");
                foreach (var key in _rows.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    if (!rows.ContainsKey(key)) changes.Add("{\"op\":\"remove\",\"key\":" + BridgeJson.Quote(key) + "}");
            }
            if (changes.Count == 0 && metadata == _metadata) return;
            var previous = Revision++;
            _metadata = metadata;
            if (!rowsUnchanged) _rows = new Dictionary<string, string>(rows, StringComparer.Ordinal);
            var chunks = Math.Max(1, (changes.Count + PageSize - 1) / PageSize);
            var frames = new List<string>(chunks);
            for (var i = 0; i < chunks; i++)
                frames.Add(Header() + ",\"previousRevision\":" + previous + ",\"chunkIndex\":" + i +
                    ",\"chunkCount\":" + chunks + ",\"changes\":[" + string.Join(",", changes.Skip(i * PageSize).Take(PageSize)) + "]}");
            Changed?.Invoke(frames);
        }

        private string Header() => "{\"schemaVersion\":2,\"generation\":" + BridgeJson.Quote(Generation) +
            ",\"revision\":" + Revision.ToString(CultureInfo.InvariantCulture) + ",\"state\":" + _metadata;

        internal string Snapshot(string connection, string snapshotId, int offset, string observationJson = "")
        {
            foreach (var expired in _snapshots.Where(p => _clock() - p.Value.CreatedAt > 30).Select(p => p.Key).ToArray())
                _snapshots.Remove(expired);
            SavedSnapshot saved;
            if (string.IsNullOrEmpty(snapshotId))
            {
                if (offset != 0) throw new ActivityError("invalid_cursor", "A new snapshot must start at offset zero.");
                if (!_snapshots.ContainsKey(connection) && _snapshots.Count >= 256)
                    _snapshots.Remove(_snapshots.OrderBy(p => p.Value.CreatedAt).First().Key);
                saved = new SavedSnapshot(Guid.NewGuid().ToString("N"), Header() + observationJson,
                    _rows.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "{\"key\":" + BridgeJson.Quote(p.Key) + ",\"value\":" + p.Value + "}").ToArray(), _clock());
                _snapshots[connection] = saved;
            }
            else if (!_snapshots.TryGetValue(connection, out saved!) || saved.Id != snapshotId)
                throw new ActivityError("snapshot_expired", "Restart snapshot pagination from offset zero.");
            if (offset < 0 || offset > saved.Rows.Length) throw new ActivityError("invalid_cursor", "Snapshot offset is out of range.");
            var end = Math.Min(saved.Rows.Length, offset + PageSize);
            return saved.Header + ",\"snapshotId\":" + BridgeJson.Quote(saved.Id) + ",\"offset\":" + offset +
                ",\"nextOffset\":" + (end < saved.Rows.Length ? end.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"rows\":[" + string.Join(",", saved.Rows.Skip(offset).Take(PageSize)) + "]}";
        }

        private sealed class SavedSnapshot
        {
            internal readonly string Id, Header;
            internal readonly string[] Rows;
            internal readonly double CreatedAt;
            internal SavedSnapshot(string id, string header, string[] rows, double createdAt)
            { Id = id; Header = header; Rows = rows; CreatedAt = createdAt; }
        }
    }

    internal sealed class ActivityError : Exception
    {
        internal readonly string Code;
        internal readonly int RetryAfterMs;
        internal ActivityError(string code, string message, int retryAfterMs = 0) : base(message)
        { Code = code; RetryAfterMs = retryAfterMs; }
    }
}
