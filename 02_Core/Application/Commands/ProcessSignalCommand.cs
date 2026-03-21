using MediatR;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Commands;

public record ProcessSignalCommand(Signal Signal) : IRequest<bool>;
