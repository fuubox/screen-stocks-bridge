using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScreenStocksBridge
{
    internal static class MarketClock
    {
        internal static string Json(bool synchronized, long nowMs, long tick, int tickMs, long epochMs, long intervalMs)
        {
            long? next = null;
            double? progress = null;
            if (synchronized && intervalMs > 0 && epochMs >= 0 && nowMs >= 0)
            {
                var elapsed = nowMs - epochMs;
                if (elapsed < 0) { next = epochMs; progress = 0; }
                else
                {
                    try { next = checked(epochMs + checked((elapsed / intervalMs + 1) * intervalMs)); }
                    catch (OverflowException) { }
                    progress = (elapsed % intervalMs) / (double)intervalMs;
                }
            }
            return "{\"synchronized\":" + (synchronized ? "true" : "false") +
                ",\"serverTimeMs\":" + (synchronized ? nowMs.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"marketTick\":" + tick + ",\"tickDurationMs\":" + tickMs +
                ",\"marketEpochMs\":" + epochMs + ",\"dividendIntervalMs\":" + intervalMs +
                ",\"nextDividendBoundaryMs\":" + (next?.ToString(CultureInfo.InvariantCulture) ?? "null") +
                ",\"remainingMs\":" + (next.HasValue ? Math.Max(0, next.Value - nowMs).ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"cycleProgress\":" + (progress?.ToString("R", CultureInfo.InvariantCulture) ?? "null") +
                ",\"clockProvenance\":\"game_server_clock\",\"scheduleProvenance\":\"local_game_settings\"}";
        }

        internal static string MaterialSnapshot(StateSnapshotDto snapshot)
        {
            // Compare a separate view, preserving full current clock information
            // in actual snapshot responses. Deadlines/configuration remain material.
            var tick = snapshot.serverTick;
            var clock = snapshot.marketClock;
            var now = snapshot.cooldowns.serverNowUnixSeconds;
            var buy = snapshot.cooldowns.buy;
            var shortTrade = snapshot.cooldowns.shortTrade;
            var buyRemaining = buy.remainingSeconds; var shortRemaining = shortTrade.remainingSeconds;
            var buyActive = buy.active; var shortActive = shortTrade.active;
            var actions = snapshot.autoActions.actions.Select(a => (Action: a, Remaining: a.cooldownRemainingSeconds, Active: a.onCooldown)).ToArray();
            try
            {
                snapshot.serverTick = 0; snapshot.marketClock = "null";
                snapshot.cooldowns.serverNowUnixSeconds = 0;
                buy.remainingSeconds = shortTrade.remainingSeconds = 0;
                buy.active = shortTrade.active = false;
                foreach (var item in actions) { item.Action.cooldownRemainingSeconds = 0; item.Action.onCooldown = false; }
                return BridgeJson.SerializeSnapshot(snapshot);
            }
            finally
            {
                snapshot.serverTick = tick; snapshot.marketClock = clock;
                snapshot.cooldowns.serverNowUnixSeconds = now;
                buy.remainingSeconds = buyRemaining; shortTrade.remainingSeconds = shortRemaining;
                buy.active = buyActive; shortTrade.active = shortActive;
                foreach (var item in actions) { item.Action.cooldownRemainingSeconds = item.Remaining; item.Action.onCooldown = item.Active; }
            }
        }
    }

    internal sealed class MarketCadence
    {
        internal double RateHz { get; private set; } = 4;
        private double _next;
        internal void SetRate(double rate)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0)
                throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be finite and positive.");
            RateHz = rate; _next = 0;
        }
        internal bool Due(double now)
        {
            if (now < _next) return false;
            _next = now + 1 / RateHz;
            return true;
        }
    }

    internal sealed class CandleUpdate
    {
        internal readonly long Sequence, BucketStartMs;
        internal readonly string Json;
        internal CandleUpdate(long sequence, long start, string json) { Sequence = sequence; BucketStartMs = start; Json = json; }
    }

    internal sealed class CandleReplay
    {
        internal bool Gap;
        internal IReadOnlyList<CandleUpdate> Updates = Array.Empty<CandleUpdate>();
    }

    internal sealed class CandleStream
    {
        private const long RetentionMs = 15000;
        private const int MaxReplayEvents = 65536;
        private readonly Dictionary<(string, long), Bar> _bars = new Dictionary<(string, long), Bar>();
        private readonly Queue<CandleUpdate> _log = new Queue<CandleUpdate>();
        private long _watermark = -1, _arrival;
        internal bool Connected { get; }
        internal CandleStream(bool connected = false) { Connected = connected; }
        internal string Generation { get; private set; } = Guid.NewGuid().ToString("N");
        internal long Sequence { get; private set; }
        internal long LateObservations { get; private set; }
        internal bool Paused { get; set; } = true;
        internal bool IsRetained(CandleUpdate update) => update.BucketStartMs + 1000 > _watermark - RetentionMs;
        internal event Action<CandleUpdate>? Updated;

        internal void Reset()
        {
            _bars.Clear(); _log.Clear(); _watermark = -1; _arrival = 0;
            Sequence = LateObservations = 0;
            Generation = Guid.NewGuid().ToString("N"); Paused = true;
        }

        internal void Advance(long serverTimeMs)
        {
            var advanced = serverTimeMs > _watermark;
            _watermark = Math.Max(_watermark, serverTimeMs);
            // Connected bars need one predecessor beyond the correction window.
            // It supplies only the opening price, never a synthetic observation.
            var historyMs = RetentionMs + (Connected ? 1000 : 0);
            foreach (var key in _bars.Keys.Where(k => k.Item2 + 1000 <= _watermark - historyMs).ToArray()) _bars.Remove(key);
            // Late corrections are appended in emission order, not bucket-time
            // order. Expired revisions can therefore occur anywhere in the log.
            if (advanced)
                for (var i = _log.Count; i > 0; i--)
                { var update = _log.Dequeue(); if (update.BucketStartMs + 1000 > _watermark - RetentionMs) _log.Enqueue(update); }
            if (Paused) return;
            foreach (var pair in _bars.OrderBy(p => p.Key.Item2).ThenBy(p => p.Key.Item1, StringComparer.Ordinal).ToArray())
                if (pair.Key.Item2 + 1000 <= _watermark && pair.Key.Item2 + 1000 > _watermark - RetentionMs && pair.Value.Revision == 0) Emit(pair.Value);
        }

        internal void Add(string stockId, double price, long sourceTimeMs, long tick)
        {
            if (Paused || string.IsNullOrEmpty(stockId) || sourceTimeMs < 0 || sourceTimeMs > long.MaxValue - 1000 || tick < 0 ||
                double.IsNaN(price) || double.IsInfinity(price) || price <= 0) return;
            Advance(sourceTimeMs);
            var second = sourceTimeMs / 1000 * 1000;
            if (sourceTimeMs < _watermark - RetentionMs) { LateObservations++; return; }
            var key = (stockId, second);
            if (!_bars.TryGetValue(key, out var bar))
            { bar = new Bar(stockId, second); _bars.Add(key, bar); }
            var firstObservation = bar.Count == 0;
            var oldClose = bar.Close;
            bar.Add(price, sourceTimeMs, tick, ++_arrival);
            if (second + 1000 <= _watermark) Emit(bar);
            // A late predecessor can change an already emitted next bar's open
            // and bounds. Recompute from its observed extrema so bounds can shrink.
            if (Connected && (firstObservation || oldClose != bar.Close) &&
                _bars.TryGetValue((stockId, second + 1000), out var next) && next.Revision > 0)
                Emit(next);
        }

        private void Emit(Bar bar)
        {
            var carried = Connected && _bars.TryGetValue((bar.StockId, bar.Start - 1000), out _);
            var open = carried ? _bars[(bar.StockId, bar.Start - 1000)].Close : bar.Open;
            bar.Revision++;
            var json = "{\"generation\":" + BridgeJson.Quote(Generation) + ",\"sequence\":" + ++Sequence +
                ",\"stockId\":" + BridgeJson.Quote(bar.StockId) + ",\"bucketStartMs\":" + bar.Start +
                ",\"bucketEndMs\":" + (bar.Start + 1000) + ",\"revision\":" + bar.Revision +
                ",\"candleMode\":" + BridgeJson.Quote(Connected ? "connected" : "observed") +
                ",\"openProvenance\":" + BridgeJson.Quote(carried ? "previous_close" : "source_observation") +
                ",\"open\":" + N(open) + ",\"high\":" + N(Math.Max(open, bar.High)) + ",\"low\":" + N(Math.Min(open, bar.Low)) + ",\"close\":" + N(bar.Close) +
                ",\"quoteCount\":" + bar.Count + ",\"quoteCountKind\":\"source_observations\",\"firstSourceTimeMs\":" + bar.First.Item1 +
                ",\"lastSourceTimeMs\":" + bar.Last.Item1 + ",\"firstTick\":" + bar.First.Item2 + ",\"lastTick\":" + bar.Last.Item2 + "}";
            var update = new CandleUpdate(Sequence, bar.Start, json);
            _log.Enqueue(update);
            while (_log.Count > MaxReplayEvents) _log.Dequeue();
            Updated?.Invoke(update);
        }

        internal CandleReplay Replay(string generation, long afterSequence)
        {
            if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
            var fresh = string.IsNullOrEmpty(generation) && afterSequence == 0;
            var gap = !fresh && (generation != Generation || afterSequence > Sequence);
            // Pruned old-bucket corrections can leave holes inside the update
            // log. Detect those as well as an expired prefix.
            if (!fresh && !gap)
            {
                var expected = afterSequence + 1;
                foreach (var update in _log.Where(e => e.Sequence > afterSequence))
                { if (update.Sequence != expected) { gap = true; break; } expected++; }
                if (expected != Sequence + 1) gap = true;
            }
            return new CandleReplay { Gap = gap, Updates = _log.Where(e => fresh || gap || e.Sequence > afterSequence).ToArray() };
        }

        private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private sealed class Bar
        {
            internal readonly string StockId;
            internal readonly long Start;
            internal double Open, High, Low, Close;
            internal long Count, Revision;
            internal (long, long, long) First, Last;
            internal Bar(string stock, long start) { StockId = stock; Start = start; }
            internal void Add(double price, long time, long tick, long arrival)
            {
                var order = (time, tick, arrival);
                if (Count++ == 0) { Open = High = Low = Close = price; First = Last = order; return; }
                High = Math.Max(High, price); Low = Math.Min(Low, price);
                if (order.CompareTo(First) < 0) { First = order; Open = price; }
                if (order.CompareTo(Last) > 0) { Last = order; Close = price; }
            }
        }
    }
}
