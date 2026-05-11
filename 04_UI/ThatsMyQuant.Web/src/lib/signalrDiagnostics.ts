import * as signalR from "@microsoft/signalr";

/**
 * Dedicated SignalR connection for the Signal Diagnostics hub. Separate from
 * the market-data hub (`signalr.ts`) to isolate cadence + back-pressure:
 * a slow diagnostics consumer must not stall market ticks.
 *
 * Server-side hub: <c>SignalDiagnosticsHub</c> at <c>/signal-diagnostics-hub</c>.
 * Broadcast event: <c>SignalDiagnosticsUpdate</c> with a SignalDiagnosticRow[] payload.
 * Group subscription via <c>JoinSignalDiagnostics</c> / <c>LeaveSignalDiagnostics</c>.
 */

const baseUrl =
  (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? "http://localhost:5266";

let connection: signalR.HubConnection | null = null;
let startPromise: Promise<void> | null = null;
let joined = false;

export function getDiagnosticsHubConnection(): signalR.HubConnection {
  if (connection) return connection;

  connection = new signalR.HubConnectionBuilder()
    .withUrl(`${baseUrl}/signal-diagnostics-hub`)
    .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  // Re-join the group after reconnect — server-side group membership is lost on disconnect.
  connection.onreconnected(() => {
    joined = false;
    void joinDiagnostics();
  });

  startPromise = connection
    .start()
    .then(() => joinDiagnostics())
    .catch((err) => {
      console.warn("[SignalR-Diagnostics] connection failed:", err);
    });

  return connection;
}

async function joinDiagnostics(): Promise<void> {
  if (!connection || joined) return;
  try {
    await connection.invoke("JoinSignalDiagnostics");
    joined = true;
  } catch (err) {
    console.warn("[SignalR-Diagnostics] JoinSignalDiagnostics failed:", err);
  }
}

export function whenDiagnosticsConnected(): Promise<void> {
  getDiagnosticsHubConnection();
  return startPromise ?? Promise.resolve();
}
