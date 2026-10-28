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
            if (module.ModuleVersionId != new Guid("19110632-9ec2-462b-bc28-0c400b4104b5"))
                throw new InvalidOperationException("Trade protection requires a reviewed game build.");
            using (var stream = File.OpenRead(module.FullyQualifiedName))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (hash != "7ce68615fbf5db153b12976208d49d73f54d75fec489ed80778ce95d0283329c")
                    throw new InvalidOperationException("Trade protection game assembly hash differs from the reviewed build.");
            }
        }
    }
}
