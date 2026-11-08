import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { GuestSession } from './core/guest/guest-session';

/**
 * Top-level routes. Each area is lazy-loaded so guests on mobile data never
 * download the admin bundle.
 *   /admin/...   photographer / couple (JWT login in Phase 1)
 *   /g/:token    guest share link (QR code target)
 */
export const routes: Routes = [
  // A guest who installed the app (PWA starts at "/") goes back to their gallery; everyone else → admin.
  {
    path: '',
    pathMatch: 'full',
    redirectTo: () => {
      const link = inject(GuestSession).lastLink();
      return link ? `/g/${link}` : '/admin';
    },
  },
  { path: 'admin', loadChildren: () => import('./admin/admin.routes').then((m) => m.ADMIN_ROUTES) },
  { path: 'g', loadChildren: () => import('./guest/guest.routes').then((m) => m.GUEST_ROUTES) },
  // /tv/:token — fullscreen slideshow for the venue screen (Full gallery link)
  { path: 'tv/:token', loadComponent: () => import('./tv/tv-page').then((m) => m.TvPage) },
  { path: '**', loadComponent: () => import('./shared/not-found/not-found').then((m) => m.NotFound) },
];
