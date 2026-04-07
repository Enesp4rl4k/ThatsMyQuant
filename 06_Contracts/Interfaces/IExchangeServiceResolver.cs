namespace ThatsMyQuant.Contracts.Interfaces;

public interface IExchangeServiceResolver
{
    IExchangeService Resolve(string exchangeName);
    bool CanResolve(string exchangeName);
}
