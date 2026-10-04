using System;
using System.Reflection;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class HumanActivityFocusService
    {
        private const float MinimumFocusChangeSeconds = 5f;
        private static readonly FieldInfo? ChartActivityFocusField = typeof(StockManager).GetField(
            "_chartActivityFocus", BindingFlags.Instance | BindingFlags.NonPublic);

        private string? _activeStockId;
        private string? _savedFocus;
        private bool _hasSavedFocus;
        private float _lastBridgeFocusChangeAt;
        private bool _hasBridgeFocusChange;

        internal string ActiveStockId => _activeStockId ?? string.Empty;

        internal bool TrySetFocus(string stockId, out HumanActivityFocusDto result,
            out string errorCode, out string errorMessage, out int retryAfterMs)
        {
            result = CreateResult();
            errorCode = string.Empty;
            errorMessage = string.Empty;
            retryAfterMs = 0;

            if (string.IsNullOrWhiteSpace(stockId) || stockId.Length > 128)
                return Fail("invalid_stock", "stockId must be a non-empty stock identifier.", out errorCode, out errorMessage);

            var game = GameManager.I;
            var manager = StockManager.I;
            if (game == null || game.Data == null || manager == null || manager.Source == null || !manager.Source.IsReady)
                return Fail("not_ready", "The online market is not ready.", out errorCode, out errorMessage);
            if (!game.IsStockVisibleToPlayer(stockId))
                return Fail("stock_unavailable", "Stock is not visible in this edition.", out errorCode, out errorMessage);
            if (!TryReadFocus(manager, out var currentFocus))
                return Fail("focus_unavailable", "The game's current activity focus could not be read.", out errorCode, out errorMessage);

            ReconcileExternalFocus(currentFocus);
            if (string.Equals(_activeStockId, stockId, StringComparison.Ordinal))
            {
                result = CreateResult();
                return true;
            }

            if (string.Equals(currentFocus, stockId, StringComparison.Ordinal))
            {
                if (!_hasSavedFocus)
                {
                    _savedFocus = currentFocus;
                    _hasSavedFocus = true;
                }
                _activeStockId = stockId;
                result = CreateResult();
                return true;
            }

            if (TryGetRetryAfter(out retryAfterMs))
                return Fail("rate_limited", "Activity focus can only be changed once every five seconds.", out errorCode, out errorMessage);

            var savedFocus = _hasSavedFocus ? _savedFocus : currentFocus;
            try
            {
                manager.SetChartActivityFocus(stockId);
            }
            catch (Exception ex)
            {
                return Fail("focus_failed", "Could not change the game's activity focus: " + ex.Message,
                    out errorCode, out errorMessage);
            }

            _savedFocus = savedFocus;
            _hasSavedFocus = true;
            _activeStockId = stockId;
            _lastBridgeFocusChangeAt = Time.unscaledTime;
            _hasBridgeFocusChange = true;
            result = CreateResult();
            return true;
        }

        internal bool TryClearFocus(out HumanActivityFocusDto result,
            out string errorCode, out string errorMessage, out int retryAfterMs)
        {
            result = CreateResult();
            errorCode = string.Empty;
            errorMessage = string.Empty;
            retryAfterMs = 0;

            if (string.IsNullOrEmpty(_activeStockId))
                return true;

            var game = GameManager.I;
            var manager = StockManager.I;
            if (game == null || game.Data == null || manager == null || manager.Source == null || !manager.Source.IsReady)
                return Fail("not_ready", "The online market is not ready.", out errorCode, out errorMessage);
            if (!TryReadFocus(manager, out var currentFocus))
                return Fail("focus_unavailable", "The game's current activity focus could not be read.", out errorCode, out errorMessage);

            if (!string.Equals(currentFocus, _activeStockId, StringComparison.Ordinal))
            {
                ClearOverrideState();
                result = CreateResult();
                return true;
            }

            var restoreFocus = _savedFocus ?? string.Empty;
            if (string.Equals(currentFocus, restoreFocus, StringComparison.Ordinal))
            {
                ClearOverrideState();
                result = CreateResult();
                return true;
            }

            if (TryGetRetryAfter(out retryAfterMs))
                return Fail("rate_limited", "Activity focus can only be changed once every five seconds.", out errorCode, out errorMessage);

            try
            {
                manager.SetChartActivityFocus(restoreFocus);
            }
            catch (Exception ex)
            {
                return Fail("focus_failed", "Could not restore the game's activity focus: " + ex.Message,
                    out errorCode, out errorMessage);
            }

            _lastBridgeFocusChangeAt = Time.unscaledTime;
            _hasBridgeFocusChange = true;
            ClearOverrideState();
            result = CreateResult();
            return true;
        }

        internal void Update()
        {
            if (string.IsNullOrEmpty(_activeStockId)) return;

            var manager = StockManager.I;
            if (manager == null || !TryReadFocus(manager, out var currentFocus) ||
                !string.Equals(currentFocus, _activeStockId, StringComparison.Ordinal))
                ClearOverrideState();
        }

        internal bool RestoreOnShutdown()
        {
            if (string.IsNullOrEmpty(_activeStockId)) return true;

            var manager = StockManager.I;
            if (manager == null || !TryReadFocus(manager, out var currentFocus))
            {
                ClearOverrideState();
                return false;
            }

            if (!string.Equals(currentFocus, _activeStockId, StringComparison.Ordinal))
            {
                ClearOverrideState();
                return true;
            }

            try
            {
                manager.SetChartActivityFocus(_savedFocus ?? string.Empty);
                ClearOverrideState();
                return true;
            }
            catch
            {
                ClearOverrideState();
                return false;
            }
        }

        private bool TryReadFocus(StockManager manager, out string focus)
        {
            focus = string.Empty;
            if (ChartActivityFocusField == null) return false;
            try
            {
                focus = ChartActivityFocusField.GetValue(manager) as string ?? string.Empty;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetRetryAfter(out int retryAfterMs)
        {
            retryAfterMs = 0;
            if (!_hasBridgeFocusChange) return false;
            var remaining = MinimumFocusChangeSeconds - (Time.unscaledTime - _lastBridgeFocusChangeAt);
            if (remaining <= 0f) return false;
            retryAfterMs = Math.Max(1, (int)Math.Ceiling(remaining * 1000f));
            return true;
        }

        private void ReconcileExternalFocus(string currentFocus)
        {
            if (!string.IsNullOrEmpty(_activeStockId) &&
                !string.Equals(currentFocus, _activeStockId, StringComparison.Ordinal))
                ClearOverrideState();
        }

        private HumanActivityFocusDto CreateResult()
        {
            return new HumanActivityFocusDto
            {
                active = !string.IsNullOrEmpty(_activeStockId),
                stockId = _activeStockId ?? string.Empty
            };
        }

        private void ClearOverrideState()
        {
            _activeStockId = null;
            _savedFocus = null;
            _hasSavedFocus = false;
        }

        private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
        {
            errorCode = code;
            errorMessage = message;
            return false;
        }
    }
}
