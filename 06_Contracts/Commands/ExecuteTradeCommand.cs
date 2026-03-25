using MediatR;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Commands;

public record ExecuteTradeCommand(Signal Signal) : IRequest<Trade>;
