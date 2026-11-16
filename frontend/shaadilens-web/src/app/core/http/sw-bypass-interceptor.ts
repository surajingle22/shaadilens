import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Keeps the Angular service worker out of API calls.
 *
 * The service worker (on in production, HTTPS only) caches the app shell; it never caches API responses. But it
 * still intercepts every request and re-sends it itself — and iPhone Safari can lose a request's body on the way
 * (photo uploads arrived with 0 bytes). The `ngsw-bypass` header tells the Angular service worker to let the
 * browser send the request directly. Same-origin only, so the extra header needs no CORS changes.
 */
export const swBypassInterceptor: HttpInterceptorFn = (req, next) =>
  req.url.startsWith('/api/') ? next(req.clone({ setHeaders: { 'ngsw-bypass': 'true' } })) : next(req);
