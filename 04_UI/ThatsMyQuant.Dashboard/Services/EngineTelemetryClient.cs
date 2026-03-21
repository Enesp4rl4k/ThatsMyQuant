using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using ThatsMyQuant.Gateway.Api.Protos;

namespace ThatsMyQuant.Dashboard.Services;

/// <summary>
/// This connects the C# WPF UI directly to the high-frequency Kestrel Engine over local network HTTP/2.
/// Replaces the old static UI with a live streaming socket that refuses to block the Desktop GUI thread.
/// </summary>
public class EngineTelemetryClient : IAsyncDisposable
{
    private GrpcChannel? _channel;
    private TradingGateway.TradingGatewayClient? _client;
    private AsyncDuplexStreamingCall<ClientSignal, EngineTelemetry>? _stream;

    public EngineTelemetryClient()
    {
        // Keep this in sync with Gateway.Api launchSettings https profile.
        _channel = GrpcChannel.ForAddress("https://localhost:7138");
        _client = new TradingGateway.TradingGatewayClient(_channel);
    }

    /// <summary>
    /// Opens the bidirectional highway. The action delegate pushes live telemetry frames straight 
    /// back into the ViewModels instantly (at 60Hz).
    /// </summary>
    public async Task StartStreamingAsync(Action<EngineTelemetry> onTelemetryReceived, CancellationToken token)
    {
        try
        {
            _stream = _client!.ConnectStream(cancellationToken: token);

            await Task.Run(async () =>
            {
                await foreach (var telemetry in _stream.ResponseStream.ReadAllAsync(token))
                {
                    // Triggers the Desktop UI to repaint numbers (PnL, Approvals) instantly
                    onTelemetryReceived?.Invoke(telemetry);
                }
            }, token);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            Debug.WriteLine("Stream gracefully disconnected from UI.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Backend Server Down: Defaulting to local UI. {ex.Message}");
        }
    }

    public async Task SendSignalAsync(ClientSignal.Types.SignalType commandType, string jsonPayload)
    {
        if (_stream != null)
        {
            await _stream.RequestStream.WriteAsync(new ClientSignal
            {
                Type = commandType,
                PayloadJson = jsonPayload
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null)
        {
            await _stream.RequestStream.CompleteAsync();
            _stream.Dispose();
        }
        if (_channel != null)
        {
            await _channel.ShutdownAsync();
            _channel.Dispose();
        }
    }
}
