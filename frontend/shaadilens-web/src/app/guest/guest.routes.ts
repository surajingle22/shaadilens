import { Routes } from '@angular/router';

/** /g/:token — everything a guest does happens on this one mobile-first page. */
export const GUEST_ROUTES: Routes = [
  { path: ':token', loadComponent: () => import('./guest-page').then((m) => m.GuestPage) },
];
