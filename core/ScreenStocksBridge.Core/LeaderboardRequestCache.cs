using System;
using System.Collections.Generic;

namespace ScreenStocksBridge
{
    internal enum LeaderboardRequestStart
    {
        Started,
        InFlight,
        RateLimited
    }

    internal sealed class LeaderboardRequestCache<T>
    {
        private const long CooldownMilliseconds = 30000;
        private const long CacheLifetimeMilliseconds = 30000;
        private readonly Dictionary<string, CachedValue> _values = new Dictionary<string, CachedValue>(StringComparer.Ordinal);
        private long _nextRequestAtMilliseconds;
        private bool _requestInFlight;

        internal LeaderboardRequestStart TryBeginFetch(long nowMilliseconds, out int retryAfterMs)
        {
            if (_requestInFlight)
            {
                retryAfterMs = 1000;
                return LeaderboardRequestStart.InFlight;
            }
            if (nowMilliseconds < _nextRequestAtMilliseconds)
            {
                retryAfterMs = (int)Math.Min(int.MaxValue, _nextRequestAtMilliseconds - nowMilliseconds);
                return LeaderboardRequestStart.RateLimited;
            }

            _requestInFlight = true;
            _nextRequestAtMilliseconds = nowMilliseconds + CooldownMilliseconds;
            retryAfterMs = 0;
            return LeaderboardRequestStart.Started;
        }

        internal bool TryGetCached(string key, long nowMilliseconds, out T value)
        {
            if (_values.TryGetValue(key, out var cached) && nowMilliseconds < cached.ExpiresAtMilliseconds)
            {
                value = cached.Value;
                return true;
            }
            _values.Remove(key);
            value = default!;
            return false;
        }

        internal void CompleteFetch(string key, T value, long nowMilliseconds)
        {
            _values[key] = new CachedValue(value, nowMilliseconds + CacheLifetimeMilliseconds);
            _requestInFlight = false;
        }

        internal void FailFetch() => _requestInFlight = false;

        private sealed class CachedValue
        {
            internal readonly T Value;
            internal readonly long ExpiresAtMilliseconds;
            internal CachedValue(T value, long expiresAtMilliseconds)
            { Value = value; ExpiresAtMilliseconds = expiresAtMilliseconds; }
        }
    }
}
