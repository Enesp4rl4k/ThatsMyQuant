using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 0 Preservation Tests.
/// These tests PASS on unfixed code — they assert that existing correct registrations
/// are NOT broken by any future changes.
/// Validates: Requirements 3.1, 3.7
/// </summary>
public class Sprint0PreservationTests
{
    private readonly IServiceCollection _services;

    public Sprint0PreservationTests()
    {
        _services = new ServiceCollection();
        _services.AddLogging();
        _services.AddThatsMyQuantApplication();
    }

    /// <summary>
    /// P1: CircuitBreakerService is registered as a singleton in the DI container.
    /// Registered at DependencyInjection.cs:31.
    /// Validates: Requirements 3.1
    /// </summary>
    [Fact]
    public void P1_CircuitBreakerService_IsRegisteredInDI()
    {
        _services.Should().Contain(
            sd => sd.ServiceType == typeof(CircuitBreakerService) &&
                  sd.Lifetime == ServiceLifetime.Singleton,
            because: "CircuitBreakerService must remain registered as a singleton in " +
                     "AddThatsMyQuantApplication() to preserve the circuit breaker guard on every order");
    }

    /// <summary>
    /// P2: MemoryPortfolioManager (Strategy namespace, 289-line implementation) is registered
    /// as a singleton in the DI container.
    /// Registered at DependencyInjection.cs:22.
    /// Validates: Requirements 3.1
    /// </summary>
    [Fact]
    public void P2_MemoryPortfolioManager_Strategy_IsRegisteredInDI()
    {
        _services.Should().Contain(
            sd => sd.ServiceType == typeof(MemoryPortfolioManager) &&
                  sd.Lifetime == ServiceLifetime.Singleton,
            because: "MemoryPortfolioManager (Strategy namespace) must remain registered as a singleton " +
                     "in AddThatsMyQuantApplication() so CircuitBreakerService and RiskAgent continue " +
                     "to receive the same instance");
    }

    /// <summary>
    /// P3: TickPipeline is registered as a singleton in the DI container.
    /// Registered at DependencyInjection.cs:29.
    /// Validates: Requirements 3.7
    /// </summary>
    [Fact]
    public void P3_TickPipeline_IsRegisteredInDI()
    {
        _services.Should().Contain(
            sd => sd.ServiceType == typeof(TickPipeline) &&
                  sd.Lifetime == ServiceLifetime.Singleton,
            because: "TickPipeline must remain registered as a singleton in AddThatsMyQuantApplication() " +
                     "to preserve the tick data feed to FeatureEngineeringAgent with unchanged semantics");
    }

    /// <summary>
    /// P4: FeatureEngineeringAgent is registered as a singleton in the DI container.
    /// Registered at DependencyInjection.cs:21.
    /// Validates: Requirements 3.7
    /// </summary>
    [Fact]
    public void P4_FeatureEngineeringAgent_IsRegisteredInDI()
    {
        _services.Should().Contain(
            sd => sd.ServiceType == typeof(FeatureEngineeringAgent) &&
                  sd.Lifetime == ServiceLifetime.Singleton,
            because: "FeatureEngineeringAgent must remain registered as a singleton in " +
                     "AddThatsMyQuantApplication() to preserve tick processing via TickPipeline");
    }
}
