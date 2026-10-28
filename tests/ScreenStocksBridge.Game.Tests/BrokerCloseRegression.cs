using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using HarmonyLib;

// Verify the new broker argument reaches native dispatch without HTTP or a scene.
internal static class BrokerCloseRegression
{
    private static bool _expected;
    private static int _calls;
    internal static void Run()
    {
        var broker = (RemoteTradeBroker)FormatterServices.GetUninitializedObject(typeof(RemoteTradeBroker));
        var harmony = new Harmony("screenstocks.bridge.close-all-regression");
        try
        {
            harmony.Patch(typeof(RemoteTradeBroker).GetMethod("SendTrade", BindingFlags.Instance | BindingFlags.NonPublic),
                prefix: new HarmonyMethod(typeof(BrokerCloseRegression), nameof(Capture)));
            foreach (var closeAll in new[] { false, true })
            {
                _expected = closeAll;
                broker.SellAsync("synthetic", BigNumber.Zero, Guid.Empty, 0f, closeAll).GetAwaiter().GetResult();
                broker.CoverAsync("synthetic", BigNumber.Zero, Guid.Empty, 0f, closeAll).GetAwaiter().GetResult();
            }
            if (_calls != 4) throw new Exception("Sell/cover dispatch was not intercepted.");
        }
        finally { harmony.UnpatchSelf(); }
    }
    private static bool Capture(bool __5, ref Task<TradeResult> __result)
    {
        if (__5 != _expected) throw new Exception("closeAll did not reach dispatch.");
        _calls++;
        __result = Task.FromResult(default(TradeResult));
        return false;
    }
}
