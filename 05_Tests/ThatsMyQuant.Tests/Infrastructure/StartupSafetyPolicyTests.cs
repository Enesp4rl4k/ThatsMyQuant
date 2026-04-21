using FluentAssertions;
using ThatsMyQuant.Infrastructure.Persistence;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public sealed class StartupSafetyPolicyTests
{
    [Fact]
    public void EnforceOrThrow_ShouldBlockOkxLiveWithoutOverride()
    {
        var act = () => StartupSafetyPolicy.EnforceOrThrow(
            serviceName: "Execution.Worker",
            selectedExchangeMode: "OKX",
            isMockMode: false,
            isOkxMode: true,
            isOkxDemo: false,
            allowLiveTrading: false,
            hasOkxCredentials: true,
            hasBinanceCredentials: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*refused to start in OKX LIVE mode*");
    }

    [Fact]
    public void EnforceOrThrow_ShouldAllowOkxDemoWithCredentials()
    {
        var act = () => StartupSafetyPolicy.EnforceOrThrow(
            serviceName: "Execution.Worker",
            selectedExchangeMode: "OKX",
            isMockMode: false,
            isOkxMode: true,
            isOkxDemo: true,
            allowLiveTrading: false,
            hasOkxCredentials: true,
            hasBinanceCredentials: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnforceOrThrow_ShouldBlockNonMockWithoutCredentials()
    {
        var act = () => StartupSafetyPolicy.EnforceOrThrow(
            serviceName: "Listener.Api",
            selectedExchangeMode: "Binance",
            isMockMode: false,
            isOkxMode: false,
            isOkxDemo: false,
            allowLiveTrading: false,
            hasOkxCredentials: false,
            hasBinanceCredentials: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*configured for exchange mode*required credentials were not found*");
    }
}
