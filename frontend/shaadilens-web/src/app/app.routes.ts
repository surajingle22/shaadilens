import { Routes } from '@angular/router';

/**
 * Top-level routes. Each area is lazy-loaded so guests on mobile data never
 * download the admin bundle.
 *   /admin/...   photographer / couple (JWT login in Phase 1)
 *   /g/:token    guest share link (QR code target)
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'admin' },
  { path: 'admin', loadChildren: () => import('./admin/admin.routes').then((m) => m.ADMIN_ROUTES) },
  { path: 'g', loadChildren: () => import('./guest/guest.routes').then((m) => m.GUEST_ROUTES) },
  { path: '**', loadComponent: () => import('./shared/not-found/not-found').then((m) => m.NotFound) },
];
