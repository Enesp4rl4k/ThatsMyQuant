import * as signalR from "@microsoft/signalr";

export type ConnectionState = "connecting" | "connected" | "reconnecting" | "disconnected";

let connection: signalR.HubConnection | null = null;
let startPromise: Promise<void> | null = null;
let currentState: ConnectionState = "connecting";

const stateListeners: Array<(s: ConnectionState) => void> = [];

function setState(s: ConnectionState) {
  currentState = s;
  stateListeners.forEach((fn) => fn(s));
}

export function getConnectionState(): ConnectionState {
  return currentState;
}

export function onConnectionStateChange(fn: (s: ConnectionState) => void): () => void {
  stateListeners.push(fn);
  return () => {
    const idx = stateListeners.indexOf(fn);
    if (idx >= 0) stateListeners.splice(idx, 1);
  };
}

const baseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? "http://localhost:5266";

export function getHubConnection(): signalR.HubConnection {
  if (connection) return connection;

  connection = new signalR.HubConnectionBuilder()
    .withUrl(`${baseUrl}/market-hub`)
    .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.onreconnecting(() => setState("reconnecting"));
  connection.onreconnected(() => setState("connected"));
  connection.onclose(() => setState("disconnected"));

  setState("connecting");
  startPromise = connection
    .start()
    .then(() => setState("connected"))
    .catch((err) => {
      console.warn("[SignalR] connection failed — falling back to polling:", err);
      setState("disconnected");
    });

  return connection;
}

export function whenConnected(): Promise<void> {
  getHubConnection();
  return startPromise ?? Promise.resolve();
}
