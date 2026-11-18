using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using ScreenStocksBridge;

// Run the bridge snapshot and native eligibility against the same controlled
// accessor. No IPO is triggered, and no scene or network is involved.
internal static class IpoPeakRegression
{
    private static GameData _data = null!;
    private static BigNumber _peak;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run()
    {
        var game = (GameManager)FormatterServices.GetUninitializedObject(typeof(GameManager));
        for (var type = typeof(GameManager); type != null; type = type.BaseType)
        {
            var pointer = type.GetField("m_CachedPtr", Fields);
            if (pointer != null) { pointer.SetValue(game, new IntPtr(1)); break; }
        }
        _data = (GameData)FormatterServices.GetUninitializedObject(typeof(GameData));
        _data.roundPeakNetWorth = (BigNumber)100f;
        typeof(GameManager).GetField("_hasRemoteIpoCatalog", Fields).SetValue(game, true);
        var singleton = typeof(GameManager).GetField("<I>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var saved = singleton.GetValue(null);
        var harmony = new Harmony("screenstocks.bridge.ipo-peak-regression");
        try
        {
            singleton.SetValue(null, game);
            Prefix(harmony, AccessTools.PropertyGetter(typeof(GameManager), "Data"), nameof(Data));
            Prefix(harmony, AccessTools.PropertyGetter(typeof(GameManager), "UsesRemoteIpoCatalog"), nameof(Ready));
            Prefix(harmony, AccessTools.Method(typeof(GameManager), "IsIPOCatalogReady"), nameof(Ready));
            Prefix(harmony, AccessTools.Method(typeof(GameManager), "IsGoingPublicUnlocked"), nameof(Ready));
            Prefix(harmony, AccessTools.Method(typeof(GameManager), "GetRunPeakNetWorthForIpo"), nameof(Peak));
            Prefix(harmony, AccessTools.Method(typeof(GameManager), "GetRequiredNetWorthForIPO"), nameof(Required));
            foreach (var value in new[] { 1200f, 50f })
            {
                _peak = (BigNumber)value;
                var json = new IpoService().Snapshot("ipo-peak");
                var expected = "\"roundPeakNetWorth\":\"" + StateService.FormatBigNumber(_peak) + "\"";
                if (!json.Contains(expected)) throw new Exception("IPO peak must follow native accessor, not stored peak 100: " + json);
                if (!json.Contains("\"eligible\":" + (value >= 1000 ? "true" : "false")))
                    throw new Exception("IPO eligibility must use the same native peak: " + json);
            }
            Console.WriteLine("PASS: IPO snapshot peak and native eligibility share the effective run-peak accessor");
        }
        finally { harmony.UnpatchSelf(); singleton.SetValue(null, saved); }
    }

    private static void Prefix(Harmony harmony, MethodInfo method, string prefix) =>
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(IpoPeakRegression), prefix));
    private static bool Data(ref GameData __result) { __result = _data; return false; }
    private static bool Ready(ref bool __result) { __result = true; return false; }
    private static bool Peak(ref BigNumber __result) { __result = _peak; return false; }
    private static bool Required(ref BigNumber __result) { __result = (BigNumber)1000f; return false; }
}
