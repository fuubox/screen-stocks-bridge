using System;
using System.IO;
using System.Security.Cryptography;

namespace ScreenStocksBridge
{
    // Harmony and coroutine adapters depend on bodies as well as signatures.
    internal static class ReviewedGameBuild
    {
        internal static void Require()
        {
            var module = typeof(StockManager).Module;
            if (module.ModuleVersionId != new Guid("dd09793d-9059-4247-9120-a2f8f1761f21"))
                throw new InvalidOperationException("Trade protection requires a reviewed game build.");
            using (var stream = File.OpenRead(module.FullyQualifiedName))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (hash != "c7f7be7089891d1740eb7b97b8a5c83d43c7cc73e82c468a7ad992700093a7d8")
                    throw new InvalidOperationException("Trade protection game assembly hash differs from the reviewed build.");
            }
        }
    }
}
