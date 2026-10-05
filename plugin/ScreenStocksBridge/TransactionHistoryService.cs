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
    internal sealed class TransactionHistoryService
    {
        internal const int MinimumCacheSeconds = 60;
        internal const int MinimumRequestIntervalSeconds = 30;
        private const int MaximumEntries = 100;
        private readonly Plugin _plugin;
        private readonly object _cacheGate = new object();
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task> _observedRequests = new Dictionary<string, Task>(StringComparer.Ordinal);
        private PendingRequest? _pending;
        private long _nextRequestAtMilliseconds;

        internal TransactionHistoryService(Plugin plugin) { _plugin = plugin; }

        internal void Handle(BridgeRequest request, BridgeConnection connection)
        {
            if (!TryParse(request.@params, out var filter, out var gameFilter, out var limit,
                    out var errorCode, out var errorMessage))
            {
                connection.Send(ProtocolJson.Error(request.id, errorCode, errorMessage), false);
                return;
            }

            var key = filter + ":" + limit.ToString(CultureInfo.InvariantCulture);
            var now = NowMilliseconds();
            if (TryGetCached(key, now, out var cached, out var ageSeconds) &&
                ageSeconds < Math.Max(MinimumCacheSeconds, _plugin.TransactionHistoryCacheSeconds.Value))
            {
                SendSnapshot(request.id, connection, cached!, filter, limit, true, false, ageSeconds, 0);
                return;
            }

            if (_pending != null)
            {
                if (cached != null)
                    SendSnapshot(request.id, connection, cached, filter, limit, true, true, ageSeconds, 1000);
                else
                    connection.Send(ProtocolJson.Error(request.id, "transaction_history_busy",
                        "A transaction-history request is already in progress.", 1000), false);
                return;
            }

            if (TryGetObservedRequest(key, out var observedTask))
            {
                _pending = new PendingRequest(request.id, connection, key, filter, limit, observedTask);
                return;
            }

            var minimumIntervalSeconds = Math.Max(MinimumRequestIntervalSeconds,
                _plugin.TransactionHistoryMinimumRequestIntervalSeconds.Value);
            var minimumIntervalMs = (long)minimumIntervalSeconds * 1000L;
            var retryAfterMs = (int)Math.Min(int.MaxValue, Math.Max(0L, _nextRequestAtMilliseconds - now));
            if (retryAfterMs > 0 && minimumIntervalMs > 0)
            {
                if (cached != null)
                    SendSnapshot(request.id, connection, cached, filter, limit, true, true, ageSeconds, retryAfterMs);
                else
                    connection.Send(ProtocolJson.Error(request.id, "rate_limited",
                        "Transaction-history requests are limited by the configured minimum interval.", retryAfterMs), false);
                return;
            }

            if (!TryStartRequest(gameFilter, limit, out var task, out var unavailableMessage))
            {
                if (cached != null)
                    SendSnapshot(request.id, connection, cached, filter, limit, true, true, ageSeconds, 0);
                else
                    connection.Send(ProtocolJson.Error(request.id, "not_ready", unavailableMessage), false);
                return;
            }

            _nextRequestAtMilliseconds = now + minimumIntervalMs;
            _pending = new PendingRequest(request.id, connection, key, filter, limit, task!);
        }

        internal void Update()
        {
            var pending = _pending;
            if (pending == null || !pending.task.IsCompleted) return;
            _pending = null;
            try
            {
                if (pending.task.IsCanceled || pending.task.IsFaulted)
                    throw pending.task.Exception?.GetBaseException() ?? new InvalidOperationException("The game cancelled its transaction-history request.");
                var snapshot = ConvertEntries(GetTaskResult(pending.task));
                var now = NowMilliseconds();
                Store(pending.key, snapshot, now, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!pending.connection.IsClosed)
                    SendSnapshot(pending.requestId, pending.connection, snapshot, pending.filter,
                        pending.limit, false, false, 0, 0);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("The game's transaction-history request failed: " + ex.GetBaseException().Message);
                if (!pending.connection.IsClosed && TryGetCached(pending.key, NowMilliseconds(), out var cached, out var ageSeconds))
                    SendSnapshot(pending.requestId, pending.connection, cached!, pending.filter,
                        pending.limit, true, true, ageSeconds, 0);
                else if (!pending.connection.IsClosed)
                    pending.connection.Send(ProtocolJson.Error(pending.requestId, "transaction_history_unavailable",
                        "The game could not retrieve transaction history."), false);
            }
        }

        internal void ObserveGameRequest(object? result, object[]? arguments)
        {
            if (!(result is Task task) || arguments == null || arguments.Length < 2) return;
            var filter = arguments[0]?.ToString() ?? string.Empty;
            if (!TryNormalizeFilter(filter, out var normalizedFilter)) return;
            if (!(arguments[1] is int limit) || limit < 1 || limit > MaximumEntries) return;
            var key = normalizedFilter + ":" + limit.ToString(CultureInfo.InvariantCulture);
            lock (_cacheGate)
            {
                _observedRequests[key] = task;
                var intervalSeconds = Math.Max(MinimumRequestIntervalSeconds,
                    _plugin.TransactionHistoryMinimumRequestIntervalSeconds.Value);
                var nextRequestAt = NowMilliseconds() + (long)intervalSeconds * 1000L;
                if (nextRequestAt > _nextRequestAtMilliseconds)
                    _nextRequestAtMilliseconds = nextRequestAt;
            }
            task.ContinueWith(completed =>
            {
                try
                {
                    if (completed.IsCanceled || completed.IsFaulted) return;
                    var snapshot = ConvertEntries(GetTaskResult(completed));
                    Store(key, snapshot, NowMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Could not cache the Transactions screen response: " + ex.GetBaseException().Message);
                }
                finally
                {
                    lock (_cacheGate)
                        if (_observedRequests.TryGetValue(key, out var active) && ReferenceEquals(active, completed))
                            _observedRequests.Remove(key);
                }
            }, TaskScheduler.Default);
        }

        private bool TryStartRequest(string filter, int limit, out Task task, out string error)
        {
            task = null!;
            error = "The game's transaction-history client is not ready.";
            var auth = ScreenStocksAuthManager.I;
            if (auth == null) return false;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var client = ReadMember(auth, "TransactionHistory");
            if (client == null) return false;
            var methods = client.GetType().GetMethods(flags);

            foreach (var candidate in methods)
            {
                if (candidate.Name != "GetAsync") continue;
                var parameters = candidate.GetParameters();
                if (parameters.Length != 2 || !parameters[0].ParameterType.IsEnum || parameters[1].ParameterType != typeof(int)) continue;
                var enumValue = Enum.Parse(parameters[0].ParameterType, filter, true);
                var result = candidate.Invoke(client, new[] { enumValue, (object)limit });
                if (result is Task requestTask)
                {
                    task = requestTask;
                    error = string.Empty;
                    return true;
                }
            }
            error = "The game's transaction-history client does not expose the expected GetAsync method.";
            return false;
        }

        private static bool TryParse(RequestParams? parameters, out string filter, out string gameFilter,
            out int limit, out string errorCode, out string errorMessage)
        {
            filter = string.Empty;
            gameFilter = string.Empty;
            limit = 0;
            errorCode = string.Empty;
            errorMessage = string.Empty;
            var rawFilter = parameters?.filter ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawFilter)) rawFilter = "both";
            if (!TryNormalizeFilter(rawFilter, out filter))
                return Fail("invalid_filter", "filter must be manual, both, or auto_action.", out errorCode, out errorMessage);
            gameFilter = filter == "auto_action" ? "AutoAction" : char.ToUpperInvariant(filter[0]) + filter.Substring(1);
            var requestedLimit = parameters?.limit ?? 0;
            if (requestedLimit < 0 || requestedLimit > MaximumEntries)
                return Fail("invalid_limit", "limit must be between 1 and 100.", out errorCode, out errorMessage);
            limit = requestedLimit == 0 ? MaximumEntries : requestedLimit;
            return true;
        }

        private static bool TryNormalizeFilter(string value, out string filter)
        {
            filter = value.Trim().Replace('-', '_').ToLowerInvariant();
            if (filter == "autoaction") filter = "auto_action";
            return filter == "manual" || filter == "both" || filter == "auto_action";
        }

        private bool TryGetCached(string key, long now, out TransactionHistorySnapshotDto? snapshot, out long ageSeconds)
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

        private void Store(string key, TransactionHistorySnapshotDto snapshot, long now, long fetchedAtUnixSeconds)
        {
            snapshot.fetchedAtUnixSeconds = fetchedAtUnixSeconds;
            lock (_cacheGate) _cache[key] = new CacheEntry(snapshot, now);
        }

        private static void SendSnapshot(string requestId, BridgeConnection connection,
            TransactionHistorySnapshotDto source, string filter, int limit,
            bool cached, bool stale, long ageSeconds, int retryAfterMs)
        {
            var response = new TransactionHistorySnapshotDto
            {
                filter = filter,
                limit = limit,
                cached = cached,
                stale = stale,
                ageSeconds = ageSeconds,
                fetchedAtUnixSeconds = source.fetchedAtUnixSeconds,
                retryAfterMs = retryAfterMs,
                entries = source.entries,
            };
            connection.Send(ProtocolJson.Response(requestId, true,
                BridgeJson.SerializeTransactionHistory(response), string.Empty), false);
        }

        private static TransactionHistorySnapshotDto ConvertEntries(object? result)
        {
            var collection = result as IEnumerable ?? ReadMember(result, "entries") as IEnumerable;
            var snapshot = new TransactionHistorySnapshotDto();
            if (collection == null) return snapshot;
            foreach (var item in collection)
            {
                if (item == null) continue;
                snapshot.entries.Add(new TransactionHistoryEntryDto
                {
                    id = ReadString(item, "id"),
                    occurredAt = FormatDate(ReadMember(item, "occurredAt")),
                    stockId = ReadString(item, "stockId"),
                    side = ReadString(item, "side"),
                    source = ReadString(item, "source"),
                    realizedReturn = ReadString(item, "realizedReturn"),
                    realizedPercent = ReadDouble(item, "realizedPercent"),
                });
            }
            return snapshot;
        }

        private static object? GetTaskResult(Task task)
        {
            return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
        }

        private static object? ReadMember(object? target, string name)
        {
            if (target == null) return null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            return target.GetType().GetProperty(name, flags)?.GetValue(target) ??
                   target.GetType().GetField(name, flags)?.GetValue(target);
        }

        private static string ReadString(object target, string name)
        {
            var value = ReadMember(target, name);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static double ReadDouble(object target, string name)
        {
            var value = ReadMember(target, name);
            if (value == null) return 0d;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return 0d; }
        }

        private static string FormatDate(object? value)
        {
            if (value is DateTime dateTime) return dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
        {
            errorCode = code;
            errorMessage = message;
            return false;
        }

        private static long NowMilliseconds() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        private sealed class CacheEntry
        {
            internal readonly TransactionHistorySnapshotDto snapshot;
            internal readonly long storedAtMilliseconds;
            internal CacheEntry(TransactionHistorySnapshotDto value, long storedAt) { snapshot = value; storedAtMilliseconds = storedAt; }
        }

        private sealed class PendingRequest
        {
            internal readonly string requestId;
            internal readonly BridgeConnection connection;
            internal readonly string key;
            internal readonly string filter;
            internal readonly int limit;
            internal readonly Task task;
            internal PendingRequest(string id, BridgeConnection client, string cacheKey, string queryFilter,
                int requestedLimit, Task requestTask)
            { requestId = id; connection = client; key = cacheKey; filter = queryFilter; limit = requestedLimit; task = requestTask; }
        }
    }
}
