import { Routes } from '@angular/router';

// Phase 1 adds: PIN screen, registration + consent, selfie capture, "Your photos".
export const GUEST_ROUTES: Routes = [
  { path: ':token', loadComponent: () => import('./landing/guest-landing').then((m) => m.GuestLanding) },
];
