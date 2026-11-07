import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { API } from '../api/api-urls';
import { AdminSession } from '../api/api.models';

/**
 * Admin session. The JWT itself lives in an HttpOnly cookie that JavaScript can't read, so we learn
 * "am I logged in?" by asking the API (GET /auth/me) once, then keep the answer in a signal.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);

  /** undefined = not checked yet, null = logged out. */
  readonly session = signal<AdminSession | null | undefined>(undefined);
  readonly isLoggedIn = computed(() => !!this.session());

  private checking?: Promise<boolean>;

  /** Resolves once we know whether the cookie is valid. Cached so guards don't hit the API every navigation. */
  ensureChecked(): Promise<boolean> {
    if (this.session() !== undefined) return Promise.resolve(this.isLoggedIn());
    this.checking ??= firstValueFrom(this.http.get<AdminSession>(API.me))
      .then((s) => {
        this.session.set(s);
        return true;
      })
      .catch(() => {
        this.session.set(null);
        return false;
      })
      .finally(() => (this.checking = undefined));
    return this.checking;
  }

  async login(email: string, password: string): Promise<void> {
    const s = await firstValueFrom(this.http.post<AdminSession>(API.login, { email, password }));
    this.session.set(s);
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post(API.logout, {}));
    } finally {
      this.session.set(null);
    }
  }

  /** Called by the HTTP interceptor when the API says 401 (cookie expired). */
  markLoggedOut(): void {
    this.session.set(null);
  }
}
