import { Routes } from '@angular/router';
import { adminGuard } from '../core/auth/admin-guard';
import { AdminLayout } from './layout/admin-layout';

export const ADMIN_ROUTES: Routes = [
  { path: 'login', loadComponent: () => import('./login/login').then((m) => m.Login), title: 'Admin login · ShaadiLens' },
  {
    path: '',
    component: AdminLayout,
    canActivate: [adminGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'events' },
      { path: 'events', loadComponent: () => import('./events/events-list').then((m) => m.EventsList), title: 'Events · ShaadiLens' },
      {
        path: 'events/:eventId',
        loadComponent: () => import('./event/event-shell').then((m) => m.EventShell),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'overview' },
          { path: 'overview', loadComponent: () => import('./event/overview/event-overview').then((m) => m.EventOverview) },
          { path: 'upload', loadComponent: () => import('./upload/uploader').then((m) => m.Uploader) },
          { path: 'photos', loadComponent: () => import('./photos/photos-page').then((m) => m.PhotosPage) },
          { path: 'share', loadComponent: () => import('./share/share-page').then((m) => m.SharePage) },
          { path: 'guests', loadComponent: () => import('./guests/guests-page').then((m) => m.GuestsPage) },
          { path: 'guests/:guestId', loadComponent: () => import('./guests/match-review').then((m) => m.MatchReview) },
          { path: 'selections', loadComponent: () => import('./selections/selections-page').then((m) => m.SelectionsPage) },
        ],
      },
      { path: 'system', loadComponent: () => import('./dashboard/dashboard').then((m) => m.Dashboard), title: 'System · ShaadiLens' },
      { path: 'settings', loadComponent: () => import('./settings/settings-page').then((m) => m.SettingsPage), title: 'Settings · ShaadiLens' },
    ],
  },
];
