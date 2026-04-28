import { api } from "./client";

export type ServiceState = "healthy" | "stale" | "down";

export interface ServiceHealth {
  serviceName: string;
  state: ServiceState;
  lastSeenUtc: string;
  startedUtc: string;
  uptimeSeconds: number;
  ageSeconds: number;
  version: string;
  machineName: string;
  processId: number;
  status: string;
  memoryMb: number;
}

export interface HealthSummary {
  totalServices: number;
  healthyCount: number;
  staleCount: number;
  downCount: number;
  now: string;
}

export interface ExchangeHealthSummary {
  venue: string;
  isConnected: boolean;
  credentialsConfigured: boolean;
  isDemoMode: boolean;
  lastSuccessUtc: string | null;
  lastError: string | null;
  consecutiveFailures: number;
}

export interface SystemHealthResponse {
  summary: HealthSummary;
  services: ServiceHealth[];
  exchange: ExchangeHealthSummary | null;
}

export const systemHealthApi = {
  get: () => api.get<SystemHealthResponse>("/api/system/health"),
};
