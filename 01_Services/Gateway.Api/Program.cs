using ThatsMyQuant.Gateway.Api.Services;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddSingleton(new ServiceRuntimeState("Gateway.Api"));
builder.Services.AddSingleton(new OperationalTelemetry("Gateway.Api"));

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<TradingGatewayService>();
app.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();
app.MapObservabilityEndpoints(app.Services);
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

await app.RunAsync();
