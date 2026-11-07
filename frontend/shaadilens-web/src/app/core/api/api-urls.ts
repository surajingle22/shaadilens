/**
 * All API calls use relative URLs. In Docker, nginx forwards /api, /health, /hubs, /media
 * to the .NET API; with `ng serve`, proxy.conf.json does the same. So there's no CORS
 * and no environment-specific base URL to configure.
 */
export const API = {
  ping: '/api/v1/ping',
  health: '/health',
} as const;
