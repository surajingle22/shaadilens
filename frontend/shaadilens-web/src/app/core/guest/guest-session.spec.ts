import { TestBed } from '@angular/core/testing';
import { GuestSession } from './guest-session';

describe('GuestSession', () => {
  let s: GuestSession;

  beforeEach(() => {
    localStorage.clear();
    s = TestBed.inject(GuestSession);
  });

  it('remembers one token per link so returning guests skip the selfie', () => {
    s.save('linkA', 'jwt-a', new Date(Date.now() + 86_400_000).toISOString());
    s.save('linkB', 'jwt-b', new Date(Date.now() + 86_400_000).toISOString());

    expect(s.open('linkA')).toBe(true);
    expect(s.token()).toBe('jwt-a');
    expect(s.open('linkB')).toBe(true);
    expect(s.token()).toBe('jwt-b');
    expect(s.open('linkC')).toBe(false);
    expect(s.token()).toBeNull();
  });

  it('drops expired tokens', () => {
    s.save('old', 'jwt', new Date(Date.now() - 1000).toISOString());
    expect(s.open('old')).toBe(false);
    expect(localStorage.getItem('sl_guest_old')).toBeNull();
  });

  it('remembers the last gallery so the installed app opens it', () => {
    expect(s.lastLink()).toBeNull();
    s.save('linkA', 'jwt-a', new Date(Date.now() + 86_400_000).toISOString());
    s.save('linkB', 'jwt-b', new Date(Date.now() + 86_400_000).toISOString());
    expect(s.lastLink()).toBe('linkB');
    s.forget('linkB');
    expect(s.lastLink()).toBeNull();
  });

  it('forget removes the token', () => {
    s.save('x', 'jwt', new Date(Date.now() + 60_000).toISOString());
    s.forget('x');
    expect(s.open('x')).toBe(false);
  });
});
