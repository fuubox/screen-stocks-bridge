using System.Globalization;

namespace ScreenStocksBridge
{
    internal sealed class IpoService
    {
        internal string Snapshot(string requestId)
        {
            var snapshot = CreateSnapshot();
            return ProtocolJson.Response(requestId, true, BridgeJson.SerializeIpoSnapshot(snapshot), string.Empty);
        }

        internal string Trigger(string requestId)
        {
            var snapshot = CreateSnapshot();
            if (!IpoTriggerPolicy.TryAuthorize(snapshot.ready, snapshot.unlocked, snapshot.eligible,
                    out var errorCode, out var errorMessage))
                return ProtocolJson.Error(requestId, errorCode, errorMessage);

            var game = GameManager.I;
            if (game == null || game.Data == null)
                return ProtocolJson.Error(requestId, "not_ready", "IPO data is not ready.");

            var countBefore = game.RawIPOCount;
            game.TriggerIPO();
            var result = "{\"status\":\"submitted\",\"ipoCountBefore\":" +
                countBefore.ToString(CultureInfo.InvariantCulture) + "}";
            return ProtocolJson.Response(requestId, true, result, string.Empty);
        }

        private static IpoSnapshotDto CreateSnapshot()
        {
            var snapshot = new IpoSnapshotDto();
            var game = GameManager.I;
            if (game == null || game.Data == null || !game.IsIPOCatalogReady()) return snapshot;

            snapshot.ready = true;
            snapshot.unlocked = game.IsGoingPublicUnlocked();
            snapshot.eligible = game.CanIPO();
            snapshot.ipoCount = game.RawIPOCount;
            snapshot.effectiveIpoCount = game.EffectiveIPOCount;
            snapshot.roundPeakNetWorth = StateService.FormatBigNumber(game.Data.roundPeakNetWorth);
            snapshot.requiredNetWorth = StateService.FormatBigNumber(game.GetRequiredNetWorthForIPO());
            return snapshot;
        }
    }
}
