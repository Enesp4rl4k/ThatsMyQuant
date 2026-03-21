using MediatR;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Commands;

public record ExecuteTradeCommand(Signal Signal) : IRequest<Trade>;
