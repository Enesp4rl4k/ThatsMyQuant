using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 1 Bug Condition Exploration Tests.
/// These tests PASS on unfixed code — they assert the bug conditions ARE present.
/// Validates: Requirements 1.2, 1.5
/// </summary>
public class Sprint1BugConditionTests
{
    /// <summary>
    /// B5: CircuitBreakerService now accepts IPortfolioManager (interface), not the concrete type.
    /// Validates: Requirements 1.2, 1.5
    /// </summary>
    [Fact]
    public void B5_CircuitBreakerService_PortfolioParameter_IsConcreteType_NotInterface()
    {
        var type = typeof(CircuitBreakerService);
        var constructors = type.GetConstructors();
        constructors.Should().HaveCount(1);

        var ctor = constructors[0];
        var portfolioParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "portfolio");
        portfolioParam.Should().NotBeNull();

        // Fixed: parameter is now IPortfolioManager (interface), not MemoryPortfolioManager
        portfolioParam!.ParameterType.IsInterface.Should().BeTrue(
            because: "Bug B5 is fixed: the 'portfolio' parameter must be an interface (IPortfolioManager) " +
                     "to allow mocking and proper dependency injection");
    }
}
