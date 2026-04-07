using System;
using System.Collections.Generic;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

internal sealed class ExecutionReportStream : IObservable<ExecutionReport>, IDisposable
{
    private readonly object _gate = new();
    private readonly List<IObserver<ExecutionReport>> _observers = new();
    private bool _isCompleted;

    public IDisposable Subscribe(IObserver<ExecutionReport> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            if (_isCompleted)
            {
                observer.OnCompleted();
                return EmptySubscription.Instance;
            }

            _observers.Add(observer);
            return new Subscription(this, observer);
        }
    }

    public void Publish(ExecutionReport report)
    {
        foreach (var observer in SnapshotObservers())
        {
            observer.OnNext(report);
        }
    }

    public void Complete()
    {
        List<IObserver<ExecutionReport>> observersToComplete;

        lock (_gate)
        {
            if (_isCompleted)
            {
                return;
            }

            _isCompleted = true;
            observersToComplete = new List<IObserver<ExecutionReport>>(_observers);
            _observers.Clear();
        }

        foreach (var observer in observersToComplete)
        {
            observer.OnCompleted();
        }
    }

    public void Dispose() => Complete();

    private void Unsubscribe(IObserver<ExecutionReport> observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }

    private List<IObserver<ExecutionReport>> SnapshotObservers()
    {
        lock (_gate)
        {
            return new List<IObserver<ExecutionReport>>(_observers);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ExecutionReportStream _owner;
        private IObserver<ExecutionReport>? _observer;

        public Subscription(ExecutionReportStream owner, IObserver<ExecutionReport> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            if (_observer is null)
            {
                return;
            }

            _owner.Unsubscribe(_observer);
            _observer = null;
        }
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static EmptySubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
