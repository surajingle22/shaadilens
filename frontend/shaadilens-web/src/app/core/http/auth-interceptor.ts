import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Auth } from '../auth/auth';
import { GuestSession } from '../guest/guest-session';

/**
 * - Adds the guest's Bearer token to /api/v1/public/* calls (the admin uses a cookie instead).
 * - If an admin API call returns 401 (cookie expired), mark logged out and go to the login page.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const router = inject(Router);
  const guest = inject(GuestSession);

  const isPublic = req.url.startsWith('/api/v1/public/');
  const token = isPublic ? guest.token() : null;
  const request = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

  return next(request).pipe(
    catchError((err: unknown) => {
      const isAdminApi = req.url.startsWith('/api/v1/') && !isPublic && !req.url.startsWith('/api/v1/auth/');
      if (err instanceof HttpErrorResponse && err.status === 401 && isAdminApi) {
        auth.markLoggedOut();
        void router.navigate(['/admin/login'], { queryParams: { returnUrl: router.url } });
      }
      return throwError(() => err);
    }),
  );
};
