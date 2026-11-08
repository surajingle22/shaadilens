import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, isDevMode, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideServiceWorker } from '@angular/service-worker';
import { routes } from './app.routes';
import { authInterceptor } from './core/http/auth-interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // withComponentInputBinding: route params (e.g. /g/:token) arrive as component inputs.
    provideRouter(routes, withComponentInputBinding()),
    // Default XHR backend (not withFetch): fetch can't report UPLOAD progress, which the uploader needs.
    provideHttpClient(withInterceptors([authInterceptor])),
    // PWA: installable app + Web Push ("new photos of you"). Off in `ng serve` (dev mode).
    // Registered once the app is idle so it never slows down the first load on mobile data.
    provideServiceWorker('ngsw-worker.js', { enabled: !isDevMode(), registrationStrategy: 'registerWhenStable:30000' }),
  ],
};
