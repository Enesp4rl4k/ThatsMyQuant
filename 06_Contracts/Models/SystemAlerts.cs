using MediatR;

namespace ThatsMyQuant.Contracts.Models;

public sealed record VpinToxicityAlert(string Symbol, double ZScore, DateTime Timestamp) : INotification;
public sealed record StaleDataAlert(string Symbol, long LatencyMs, DateTime Timestamp) : INotification;
