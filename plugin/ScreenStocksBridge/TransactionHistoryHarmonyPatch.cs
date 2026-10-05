using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal static class TransactionHistoryHarmonyPatch
    {
        internal static TransactionHistoryService? CaptureService;

        internal static void Install(Harmony harmony)
        {
            var assembly = typeof(ScreenStocksAuthManager).Assembly;
            var type = assembly.GetType("ScreenStocks.Net.TransactionHistoryClient") ??
                       assembly.GetTypes().FirstOrDefault(candidate => candidate.Name == "TransactionHistoryClient");
            var target = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GetAsync" && method.GetParameters().Length == 2);
            if (target == null) throw new MissingMethodException("TransactionHistoryClient.GetAsync(filter, limit) was not found.");
            var postfix = new HarmonyMethod(typeof(TransactionHistoryHarmonyPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(target, postfix: postfix);
        }

        private static void Postfix(object? __result, object[] __args)
        {
            CaptureService?.ObserveGameRequest(__result, __args);
        }
    }
}
