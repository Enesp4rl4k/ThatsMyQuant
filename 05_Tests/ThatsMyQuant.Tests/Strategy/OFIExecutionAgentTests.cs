using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class OFIExecutionAgentTests
{
    [Fact]
    public async Task Handle_ShouldNoOp_WhenConstructedWithoutExchangeInfoProvider()
    {
        var agent = new OFIExecutionAgent(NullLogger<OFIExecutionAgent>.Instance);
        var signal = new Signal
        {
            Symbol = "BTCUSDT",
            Price = 60000m,
            Timestamp = System.DateTime.UtcNow,
            ExecutionAction = ExecutionActionType.None
        };

        await agent.Handle(signal, CancellationToken.None);

        Assert.Equal(ExecutionActionType.None, signal.ExecutionAction);
    }
}
