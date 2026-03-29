using Microsoft.AspNetCore.SignalR.Client;
using System.Diagnostics;

namespace ThatsMyQuant.Dashboard.Services;

public interface IWebVitalsService
{
    Task UpdateVitalsAsync(double protection, double investment, double fitness);
    Task StartAsync();
}

public class WebVitalsService : IWebVitalsService, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private bool _isConnected;

    public WebVitalsService()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:5202/marketdata")
            .WithAutomaticReconnect()
            .Build();
    }

    public async Task StartAsync()
    {
        if (_isConnected) return;

        try
        {
            await _connection.StartAsync();
            _isConnected = true;
            Debug.WriteLine("WPF: Connected to Web Vitals Hub.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WPF: Failed to connect to Web Vitals Hub: {ex.Message}");
        }
    }

    public async Task UpdateVitalsAsync(double protection, double investment, double fitness)
    {
        if (_connection.State != HubConnectionState.Connected)
        {
            await StartAsync();
        }

        if (_connection.State == HubConnectionState.Connected)
        {
            try
            {
                await _connection.InvokeAsync("UpdateSystemVitals", protection, investment, fitness);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WPF: Failed to send vitals: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
        }
    }
}
