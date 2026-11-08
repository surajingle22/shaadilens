import { Injectable, signal } from '@angular/core';

/**
 * Guest JWTs, one per share link, kept in localStorage so reopening the link skips the selfie.
 * (A guest token only grants "see my photos at this one wedding", and expires after 30 days.)
 */
@Injectable({ providedIn: 'root' })
export class GuestSession {
  private static readonly prefix = 'sl_guest_';
  private static readonly lastKey = 'sl_last_link';

  /** Token for the link currently open (sent as Bearer on /api/v1/public/*). */
  readonly token = signal<string | null>(null);

  /** Switch to a link: loads its stored token, if any. Returns true if one was found. */
  open(linkToken: string): boolean {
    const stored = this.read(linkToken);
    this.token.set(stored);
    return stored !== null;
  }

  save(linkToken: string, jwt: string, expiresAt: string): void {
    this.token.set(jwt);
    try {
      localStorage.setItem(GuestSession.prefix + linkToken, JSON.stringify({ jwt, expiresAt }));
      localStorage.setItem(GuestSession.lastKey, linkToken);
    } catch {
      // Private browsing / storage blocked: the session still works until the tab closes.
    }
  }

  forget(linkToken: string): void {
    this.token.set(null);
    try {
      localStorage.removeItem(GuestSession.prefix + linkToken);
      if (localStorage.getItem(GuestSession.lastKey) === linkToken) localStorage.removeItem(GuestSession.lastKey);
    } catch {
      /* ignore */
    }
  }

  /**
   * The gallery link this phone used last, if its session is still valid. The installed app (PWA) starts
   * at "/", which sends guests straight back to their photos instead of the admin login.
   */
  lastLink(): string | null {
    try {
      const link = localStorage.getItem(GuestSession.lastKey);
      return link && this.read(link) ? link : null;
    } catch {
      return null;
    }
  }

  private read(linkToken: string): string | null {
    try {
      const raw = localStorage.getItem(GuestSession.prefix + linkToken);
      if (!raw) return null;
      const { jwt, expiresAt } = JSON.parse(raw) as { jwt: string; expiresAt: string };
      if (new Date(expiresAt).getTime() < Date.now()) {
        localStorage.removeItem(GuestSession.prefix + linkToken);
        return null;
      }
      return jwt;
    } catch {
      return null;
    }
  }
}
