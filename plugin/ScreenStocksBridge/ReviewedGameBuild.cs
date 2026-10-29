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
            if (module.ModuleVersionId != new Guid("20f0cbb7-bcb4-4ccf-9354-ec2ae5d91b51"))
                throw new InvalidOperationException("Trade protection requires a reviewed game build.");
            using (var stream = File.OpenRead(module.FullyQualifiedName))
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (hash != "6f396487493579564e6739daea7413295eefbc559ccf3690c68fd602468cf3a1")
                    throw new InvalidOperationException("Trade protection game assembly hash differs from the reviewed build.");
            }
        }
    }
}
