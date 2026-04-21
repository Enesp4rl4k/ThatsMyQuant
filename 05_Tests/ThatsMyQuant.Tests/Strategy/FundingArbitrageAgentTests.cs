using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class FundingArbitrageAgentTests
{
    [Fact]
    public async Task Handle_ShouldNoOp_WhenConstructedWithoutFundingProvider()
    {
        var mediator = Substitute.For<IMediator>();
        var agent = new FundingArbitrageAgent(NullLogger<FundingArbitrageAgent>.Instance, mediator);
        var signal = new Signal
        {
            Symbol = "BTC-USDT",
            Price = 60000m,
            Timestamp = System.DateTime.UtcNow,
            ExecutionAction = ExecutionActionType.None
        };

        await agent.Handle(signal, CancellationToken.None);

        await mediator.DidNotReceiveWithAnyArgs().Send(default!, default);
    }
}
