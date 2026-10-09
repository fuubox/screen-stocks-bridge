using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal static class NetWorthHistoryHarmonyPatch
    {
        internal static NetWorthHistoryService? CaptureService;

        internal static void Install(Harmony harmony)
        {
            var type = typeof(ScreenStocksAuthManager).Assembly.GetType("ScreenStocks.Net.NetWorthHistoryClient") ??
                       typeof(ScreenStocksAuthManager).Assembly.GetTypes()
                           .FirstOrDefault(candidate => candidate.Name == "NetWorthHistoryClient");
            var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "FetchAsync" && method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType.IsEnum);
            if (target == null)
                throw new MissingMethodException("NetWorthHistoryClient.FetchAsync(range) was not found.");
            var postfix = new HarmonyMethod(typeof(NetWorthHistoryHarmonyPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(target, postfix: postfix);
        }

        private static void Postfix(object? __result, object[] __args)
        {
            CaptureService?.ObserveGameRequest(__result, __args);
        }
    }
}
