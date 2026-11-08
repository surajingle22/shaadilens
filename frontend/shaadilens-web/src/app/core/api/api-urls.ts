/**
 * All API calls use relative URLs. In Docker, nginx forwards /api, /health, /hubs, /media
 * to the .NET API; with `ng serve`, proxy.conf.json does the same. So there's no CORS
 * and no environment-specific base URL to configure.
 */
const v1 = '/api/v1';

export const API = {
  ping: `${v1}/ping`,
  health: '/health',

  login: `${v1}/auth/login`,
  logout: `${v1}/auth/logout`,
  me: `${v1}/auth/me`,

  events: `${v1}/events`,
  event: (id: string) => `${v1}/events/${id}`,
  publish: (id: string) => `${v1}/events/${id}/publish`,
  stats: (id: string) => `${v1}/events/${id}/stats`,
  faceSettings: (id: string) => `${v1}/events/${id}/face-settings`,
  cover: (id: string) => `${v1}/events/${id}/cover`,
  collections: (id: string) => `${v1}/events/${id}/collections`,
  collection: (eventId: string, id: string) => `${v1}/events/${eventId}/collections/${id}`,
  collectionsOrder: (id: string) => `${v1}/events/${id}/collections/order`,
  eventPhotos: (id: string) => `${v1}/events/${id}/photos`,
  retryFailed: (id: string) => `${v1}/events/${id}/photos/retry-failed`,

  photo: (id: string) => `${v1}/photos/${id}`,
  reprocess: (id: string) => `${v1}/photos/${id}/reprocess`,

  initUpload: (eventId: string) => `${v1}/events/${eventId}/uploads`,
  upload: (uploadId: string) => `${v1}/uploads/${uploadId}`,
  uploadChunk: (uploadId: string, index: number) => `${v1}/uploads/${uploadId}/chunks/${index}`,
  completeUpload: (uploadId: string) => `${v1}/uploads/${uploadId}/complete`,

  shareLinks: (eventId: string) => `${v1}/events/${eventId}/share-links`,
  shareLink: (id: string) => `${v1}/share-links/${id}`,
  shareLinkPin: (id: string) => `${v1}/share-links/${id}/pin`,
  qr: (id: string) => `${v1}/share-links/${id}/qr.png`,
  poster: (id: string, size: 'a4' | 'a5') => `${v1}/share-links/${id}/poster.png?size=${size}`,

  guests: (eventId: string) => `${v1}/events/${eventId}/guests`,
  guest: (id: string) => `${v1}/guests/${id}`,
  guestSearch: (id: string) => `${v1}/guests/${id}/search`,
  guestMatches: (id: string) => `${v1}/guests/${id}/matches`,
  match: (id: string) => `${v1}/matches/${id}`,
  selfieImage: (referenceId: string) => `${v1}/guest-references/${referenceId}/image`,
  thresholdPreview: (eventId: string) => `${v1}/events/${eventId}/face-settings/preview`,
  rematch: (eventId: string) => `${v1}/events/${eventId}/rematch`,
  guestNotify: (id: string) => `${v1}/guests/${id}/notify`,

  settings: `${v1}/settings`,
  testEmail: `${v1}/settings/test-email`,
  vapidKeys: `${v1}/settings/vapid-keys`,

  hangfire: '/hangfire',
} as const;
