import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './core/http/auth-interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // withComponentInputBinding: route params (e.g. /g/:token) arrive as component inputs.
    provideRouter(routes, withComponentInputBinding()),
    // Default XHR backend (not withFetch): fetch can't report UPLOAD progress, which the uploader needs.
    provideHttpClient(withInterceptors([authInterceptor])),
  ],
};
