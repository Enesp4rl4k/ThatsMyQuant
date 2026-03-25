using MediatR;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Commands;

public record ProcessSignalCommand(Signal Signal) : IRequest<bool>;
