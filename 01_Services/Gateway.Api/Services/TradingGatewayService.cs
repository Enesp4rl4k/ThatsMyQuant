using Grpc.Core;
using ThatsMyQuant.Gateway.Api.Protos;

namespace ThatsMyQuant.Gateway.Api.Services;

/// <summary>
/// The live Kestrel-hosted interface for the WPF client. 
/// Utilizes Bidirectional streaming to push Machine Learning probability and execution telemetry directly 
/// into the UI dashboard at 60 Hz without typical REST JSON parsing overhead.
/// </summary>
public class TradingGatewayService : TradingGateway.TradingGatewayBase
{
    private readonly ILogger<TradingGatewayService> _logger;

    public TradingGatewayService(ILogger<TradingGatewayService> logger)
    {
        _logger = logger;
    }

    public override async Task ConnectStream(
        IAsyncStreamReader<ClientSignal> requestStream, 
        IServerStreamWriter<EngineTelemetry> responseStream, 
        ServerCallContext context)
    {
        _logger.LogInformation("WPF Client connected to High-Frequency Telemetry Stream.");

        // Read signals from the WPF Dashboard (Arm, Disarm, Adjust Risk)
        var clientTask = Task.Run(async () =>
        {
            await foreach (var message in requestStream.ReadAllAsync())
            {
                _logger.LogInformation($"Client UI Signal Received: {message.Type} - {message.PayloadJson}");
                // In production, this forwards the command into the Swarm Orchestrator
            }
        });

        // Write streaming telemetry (PnL, Position Counts, ML Confidence) down to the UI continuously
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var telemetry = new EngineTelemetry
            {
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                ActivePnl = 1450.50, // Mock live calculation
                MarginUtilized = 12.5,
                LatestExecutionLog = "ML.NET Risk Swarm Authorized Trade at $65,400.",
                OpenPositionsCount = 1,
                SwarmConsensusStatus = "Unanimous Approvals [Sentiment, Risk, Inference]"
            };

            await responseStream.WriteAsync(telemetry);
            
            // Push frames down to the WPF client roughly every 100 milliseconds
            await Task.Delay(100); 
        }

        await clientTask;
        _logger.LogInformation("WPF Telemetry Stream Terminated.");
    }
}
