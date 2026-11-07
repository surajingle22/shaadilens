/** Mirrors ShaadiLens.Api.Controllers.PingResponse */
export interface PingResponse {
  name: string;
  version: string;
  environment: string;
  serverTimeUtc: string;
}

/** Shape written by the API's HealthResponseWriter at GET /health */
export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  totalMs: number;
  checks: Record<string, { status: string; durationMs: number; description?: string | null }>;
}
