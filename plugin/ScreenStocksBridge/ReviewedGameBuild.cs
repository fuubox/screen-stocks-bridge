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
            if (module.ModuleVersionId != new Guid("147c912a-2142-488b-9aa1-ba219f725d25"))
                throw new InvalidOperationException("Trade protection requires a reviewed game build.");
            using (var stream = File.OpenRead(module.FullyQualifiedName))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (hash != "55be35ae6b44b3d45542ebccc43c3dfa4db69d6b5c262ee34ba5bb624b04ee3d")
                    throw new InvalidOperationException("Trade protection game assembly hash differs from the reviewed build.");
            }
        }
    }
}
