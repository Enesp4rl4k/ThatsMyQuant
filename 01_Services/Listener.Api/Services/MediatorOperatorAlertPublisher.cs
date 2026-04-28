using MediatR;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Notifications;

namespace Listener.Api.Services;

/// <summary>
/// In-process publisher. Listener.Api hosts the SignalR hub and the
/// <see cref="RealtimeBroadcaster"/> handler, so we just bounce through
/// MediatR and the existing pipeline does the rest.
/// </summary>
public sealed class MediatorOperatorAlertPublisher : IOperatorAlertPublisher
{
    private readonly IMediator _mediator;

    public MediatorOperatorAlertPublisher(IMediator mediator)
    {
        _mediator = mediator;
    }

    public Task PublishAsync(string severity, string source, string title, string body, CancellationToken ct = default)
        => _mediator.Publish(
            new OperatorAlertNotification(severity, source, title, body, DateTime.UtcNow),
            ct);
}
