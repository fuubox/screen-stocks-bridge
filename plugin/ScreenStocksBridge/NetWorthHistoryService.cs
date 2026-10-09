using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal sealed class NetWorthHistoryService
    {
        internal const int MinimumCacheSeconds = 60;
        internal const int MinimumRequestIntervalSeconds = 30;
        private readonly Plugin _plugin;
        private readonly object _cacheGate = new object();
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task> _observedRequests = new Dictionary<string, Task>(StringComparer.Ordinal);
        private PendingRequest? _pending;
        private long _nextRequestAtMilliseconds;

        internal NetWorthHistoryService(Plugin plugin) { _plugin = plugin; }

        internal void Handle(BridgeRequest request, BridgeConnection connection)
        {
            if (!TryParse(request.@params?.range, out var range, out var enumName))
            {
                connection.Send(ProtocolJson.Error(request.id, "invalid_range",
                    "range must be last_24_hours, last_7_days, or last_14_days."), false);
                return;
            }

            var now = NowMilliseconds();
            TryGetCached(range, now, out var cached, out var ageSeconds);
            var cacheSeconds = Math.Max(MinimumCacheSeconds, _plugin.NetWorthHistoryCacheSeconds.Value);
            if (cached != null && ageSeconds < cacheSeconds)
            {
                SendSnapshot(request.id, connection, cached, range, true, false, ageSeconds, 0);
                return;
            }

            if (_pending != null)
            {
                if (cached != null)
                    SendSnapshot(request.id, connection, cached, range, true, true, ageSeconds, 1000);
                else
                    connection.Send(ProtocolJson.Error(request.id, "net_worth_history_busy",
                        "A net-worth history request is already in progress.", 1000), false);
                return;
            }

            var key = range;
            if (TryGetObservedRequest(key, out var observedTask))
            {
                _pending = new PendingRequest(request.id, connection, key, range, observedTask);
                return;
            }

            var intervalSeconds = Math.Max(MinimumRequestIntervalSeconds,
                _plugin.NetWorthHistoryMinimumRequestIntervalSeconds.Value);
            var retryAfterMs = (int)Math.Min(int.MaxValue, Math.Max(0L,
                _nextRequestAtMilliseconds - now));
            if (retryAfterMs > 0)
            {
                if (cached != null)
                    SendSnapshot(request.id, connection, cached, range, true, true, ageSeconds, retryAfterMs);
                else
                    connection.Send(ProtocolJson.Error(request.id, "rate_limited",
                        "Net-worth history requests are limited by the configured minimum interval.", retryAfterMs), false);
                return;
            }

            if (!TryStartRequest(enumName, out var task))
            {
                if (cached != null)
                    SendSnapshot(request.id, connection, cached, range, true, true, ageSeconds, 0);
                else
                    connection.Send(ProtocolJson.Error(request.id, "not_ready",
                        "The game's net-worth history client is not ready."), false);
                return;
            }

            _nextRequestAtMilliseconds = now + (long)intervalSeconds * 1000L;
            _pending = new PendingRequest(request.id, connection, key, range, task!);
        }

        internal void Update()
        {
            var pending = _pending;
            if (pending == null || !pending.task.IsCompleted) return;
            _pending = null;
            try
            {
                if (pending.task.IsCanceled || pending.task.IsFaulted)
                    throw pending.task.Exception?.GetBaseException() ??
                        new InvalidOperationException("The game cancelled its net-worth history request.");
                var snapshot = ConvertResponse(GetTaskResult(pending.task));
                var now = NowMilliseconds();
                Store(pending.key, snapshot, now, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!pending.connection.IsClosed)
                    SendSnapshot(pending.requestId, pending.connection, snapshot, pending.range,
                        false, false, 0, 0);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("The game's net-worth history request failed: " + ex.GetBaseException().Message);
                if (!pending.connection.IsClosed && TryGetCached(pending.key, NowMilliseconds(), out var cached, out var ageSeconds))
                    SendSnapshot(pending.requestId, pending.connection, cached!, pending.range,
                        true, true, ageSeconds, 0);
                else if (!pending.connection.IsClosed)
                    pending.connection.Send(ProtocolJson.Error(pending.requestId, "net_worth_history_unavailable",
                        "The game could not retrieve net-worth history."), false);
            }
        }

        internal void ObserveGameRequest(object? result, object[]? arguments)
        {
            if (!(result is Task task) || arguments == null || arguments.Length < 1 ||
                !TryParse(arguments[0]?.ToString(), out var range, out _)) return;
            lock (_cacheGate)
            {
                _observedRequests[range] = task;
                var intervalSeconds = Math.Max(MinimumRequestIntervalSeconds,
                    _plugin.NetWorthHistoryMinimumRequestIntervalSeconds.Value);
                var nextRequestAt = NowMilliseconds() + (long)intervalSeconds * 1000L;
                if (nextRequestAt > _nextRequestAtMilliseconds)
                    _nextRequestAtMilliseconds = nextRequestAt;
            }
            task.ContinueWith(completed =>
            {
                try
                {
                    if (completed.IsCanceled || completed.IsFaulted) return;
                    var snapshot = ConvertResponse(GetTaskResult(completed));
                    Store(range, snapshot, NowMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Could not cache the Net Worth screen response: " + ex.GetBaseException().Message);
                }
                finally
                {
                    lock (_cacheGate)
                        if (_observedRequests.TryGetValue(range, out var active) && ReferenceEquals(active, completed))
                            _observedRequests.Remove(range);
                }
            }, TaskScheduler.Default);
        }

        private static bool TryParse(string? value, out string range, out string enumName)
        {
            var normalized = (value ?? string.Empty).Trim().Replace('-', '_').ToLowerInvariant();
            switch (normalized)
            {
                case "":
                case "last_24_hours":
                case "last24hours":
                case "24h":
                    range = "last_24_hours";
                    enumName = "Last24Hours";
                    return true;
                case "last_7_days":
                case "last7days":
                case "7d":
                    range = "last_7_days";
                    enumName = "Last7Days";
                    return true;
                case "last_14_days":
                case "last14days":
                case "14d":
                    range = "last_14_days";
                    enumName = "Last14Days";
                    return true;
                default:
                    range = string.Empty;
                    enumName = string.Empty;
                    return false;
            }
        }

        private static bool TryStartRequest(string enumName, out Task? task)
        {
            task = null;
            var client = ScreenStocksAuthManager.I?.NetWorthHistory;
            if (client == null) return false;
            try
            {
                var range = (NetWorthHistoryRange)Enum.Parse(typeof(NetWorthHistoryRange), enumName, false);
                task = client.FetchAsync(range);
                return task != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool TryGetCached(string key, long now, out NetWorthHistorySnapshotDto? snapshot, out long ageSeconds)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    snapshot = entry.snapshot;
                    ageSeconds = Math.Max(0, (now - entry.storedAtMilliseconds) / 1000L);
                    return true;
                }
            }
            snapshot = null;
            ageSeconds = 0;
            return false;
        }

        private bool TryGetObservedRequest(string key, out Task task)
        {
            lock (_cacheGate) return _observedRequests.TryGetValue(key, out task!);
        }

        private void Store(string key, NetWorthHistorySnapshotDto snapshot, long now, long fetchedAtUnixSeconds)
        {
            snapshot.fetchedAtUnixSeconds = fetchedAtUnixSeconds;
            lock (_cacheGate) _cache[key] = new CacheEntry(snapshot, now);
        }

        private static NetWorthHistorySnapshotDto ConvertResponse(object? result)
        {
            var samples = ReadMember(result, "samples") as IEnumerable;
            var snapshot = new NetWorthHistorySnapshotDto
            {
                intervalMinutes = ReadInt(result, "intervalMinutes"),
            };
            if (samples == null) return snapshot;
            foreach (var item in samples)
            {
                if (item == null) continue;
                snapshot.samples.Add(new NetWorthHistorySampleDto
                {
                    at = ReadString(item, "at"),
                    netWorth = ReadDouble(item, "netWorth"),
                });
            }
            return snapshot;
        }

        private static object? GetTaskResult(Task task) =>
            task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);

        private static object? ReadMember(object? target, string name)
        {
            if (target == null) return null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            return target.GetType().GetProperty(name, flags)?.GetValue(target) ??
                   target.GetType().GetField(name, flags)?.GetValue(target);
        }

        private static string ReadString(object target, string name) =>
            Convert.ToString(ReadMember(target, name), CultureInfo.InvariantCulture) ?? string.Empty;

        private static int ReadInt(object? target, string name)
        {
            try { return Convert.ToInt32(ReadMember(target, name), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static double ReadDouble(object target, string name)
        {
            try { return Convert.ToDouble(ReadMember(target, name), CultureInfo.InvariantCulture); }
            catch { return 0d; }
        }

        private static void SendSnapshot(string requestId, BridgeConnection connection,
            NetWorthHistorySnapshotDto source, string range, bool cached, bool stale,
            long ageSeconds, int retryAfterMs)
        {
            var response = new NetWorthHistorySnapshotDto
            {
                range = range,
                intervalMinutes = source.intervalMinutes,
                cached = cached,
                stale = stale,
                ageSeconds = ageSeconds,
                fetchedAtUnixSeconds = source.fetchedAtUnixSeconds,
                retryAfterMs = retryAfterMs,
                samples = source.samples,
            };
            connection.Send(ProtocolJson.Response(requestId, true,
                BridgeJson.SerializeNetWorthHistory(response), string.Empty), false);
        }

        private static long NowMilliseconds() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        private sealed class CacheEntry
        {
            internal readonly NetWorthHistorySnapshotDto snapshot;
            internal readonly long storedAtMilliseconds;
            internal CacheEntry(NetWorthHistorySnapshotDto value, long storedAt)
            { snapshot = value; storedAtMilliseconds = storedAt; }
        }

        private sealed class PendingRequest
        {
            internal readonly string requestId;
            internal readonly BridgeConnection connection;
            internal readonly string key;
            internal readonly string range;
            internal readonly Task task;
            internal PendingRequest(string id, BridgeConnection client, string cacheKey,
                string requestedRange, Task requestTask)
            { requestId = id; connection = client; key = cacheKey; range = requestedRange; task = requestTask; }
        }
    }
}
