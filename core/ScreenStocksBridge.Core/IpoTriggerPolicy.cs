namespace ScreenStocksBridge
{
    internal static class IpoTriggerPolicy
    {
        internal static bool TryAuthorize(bool ready, bool unlocked, bool eligible,
            out string errorCode, out string errorMessage)
        {
            errorCode = string.Empty;
            errorMessage = string.Empty;
            if (!ready)
            {
                errorCode = "not_ready";
                errorMessage = "IPO data is not ready.";
                return false;
            }
            if (!unlocked)
            {
                errorCode = "ipo_locked";
                errorMessage = "Going public is still locked.";
                return false;
            }
            if (!eligible)
            {
                errorCode = "ipo_requirements_not_met";
                errorMessage = "The game reports that IPO requirements are not met.";
                return false;
            }
            return true;
        }
    }
}
