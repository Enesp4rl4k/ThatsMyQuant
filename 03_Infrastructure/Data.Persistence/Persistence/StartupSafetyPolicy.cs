namespace ThatsMyQuant.Infrastructure.Persistence;

public static class StartupSafetyPolicy
{
    public static string ResolveCredentialSource(bool hasOkxCredentials, bool hasBinanceCredentials)
    {
        if (hasOkxCredentials || hasBinanceCredentials)
        {
            return "configured";
        }

        return "missing";
    }

    public static void EnforceOrThrow(
        string serviceName,
        string selectedExchangeMode,
        bool isMockMode,
        bool isOkxMode,
        bool isOkxDemo,
        bool allowLiveTrading,
        bool hasOkxCredentials,
        bool hasBinanceCredentials)
    {
        if (isOkxMode && !isOkxDemo && !allowLiveTrading)
        {
            throw new InvalidOperationException(
                $"{serviceName} refused to start in OKX LIVE mode. " +
                "For safety, OKX requires demo mode by default. " +
                "Set OKX__IsDemoMode=true (recommended) or explicitly acknowledge live trading by setting TMQ_ALLOW_LIVE_TRADING=true.");
        }

        if (!isMockMode && !hasOkxCredentials && !hasBinanceCredentials)
        {
            throw new InvalidOperationException(
                $"{serviceName} is configured for exchange mode '{selectedExchangeMode}', but required credentials were not found. " +
                "Set TMQ_EXCHANGE_MODE to 'Mock' to run without live credentials, or provide the required OKX/Binance keys via " +
                "exchange_credentials.dpapi or environment variables (Binance: TMQ_BINANCE_API_KEY/TMQ_BINANCE_API_SECRET; " +
                "OKX: OKX__ApiKey/OKX__SecretKey/OKX__Passphrase).");
        }
    }
}
