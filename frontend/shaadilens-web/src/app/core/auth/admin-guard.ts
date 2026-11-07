import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Auth } from './auth';

/** Admin pages: if the auth cookie isn't valid, go to the login page and come back afterwards. */
export const adminGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);
  return (await auth.ensureChecked()) || router.createUrlTree(['/admin/login'], { queryParams: { returnUrl: state.url } });
};
