import { Routes } from '@angular/router';
import { AdminLayout } from './layout/admin-layout';

// Phase 1 adds: login page + auth guard, events, uploader, share links, guests.
export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    component: AdminLayout,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./dashboard/dashboard').then((m) => m.Dashboard) },
    ],
  },
];
