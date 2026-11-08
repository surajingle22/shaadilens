import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { API } from './core/api/api-urls';

describe('App routes', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes, withComponentInputBinding()), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  it('sends a signed-out visitor from / to the admin login', async () => {
    const harnessPromise = RouterTestingHarness.create('/');
    await vi.waitFor(() => http.expectOne(API.me).flush(null, { status: 401, statusText: 'Unauthorized' }), { timeout: 5000 }); // first lazy route load can be slow on a cold CI box
    const harness = await harnessPromise;
    expect(harness.routeNativeElement?.textContent).toContain('ShaadiLens admin');
  });

  it('opens the events page when the admin cookie is valid', async () => {
    const harnessPromise = RouterTestingHarness.create('/admin');
    await vi.waitFor(() => http.expectOne(API.me).flush({ email: 'a@b.c', expiresAt: null }));
    const harness = await harnessPromise;
    await vi.waitFor(() => http.expectOne(API.events).flush([]));
    await harness.fixture.whenStable();
    expect(harness.routeNativeElement?.textContent).toContain('Events');
  });

  it('shows the guest landing page with the event from the link', async () => {
    localStorage.clear();
    const harnessPromise = RouterTestingHarness.create('/g/abc123');
    await vi.waitFor(() =>
      http.expectOne('/api/v1/public/links/abc123').flush({
        event: { name: 'Suraj & Priya', date: '2026-12-12', venue: 'Pune', themeColor: '#b0004a', titleFont: null, coverUrl: null },
        linkType: 'Guest', requiresPin: false, requireRegistration: true, registrationFields: 'Name',
        selfieRequired: true, faceSearchEnabled: true,
      }),
    );
    const harness = await harnessPromise;
    await vi.waitFor(() => {
      harness.detectChanges();
      const text = harness.routeNativeElement?.textContent ?? '';
      expect(text).toContain('Suraj & Priya');
      expect(text).toContain('Find your photos');
    });
  });

  it('shows a friendly message for a closed gallery', async () => {
    localStorage.clear();
    const harnessPromise = RouterTestingHarness.create('/g/closed1');
    await vi.waitFor(() =>
      http.expectOne('/api/v1/public/links/closed1').flush(
        { status: 403, detail: 'This gallery isn’t open right now. Please check with the couple.' },
        { status: 403, statusText: 'Forbidden' },
      ),
    );
    const harness = await harnessPromise;
    await vi.waitFor(() => {
      harness.detectChanges();
      expect(harness.routeNativeElement?.textContent).toContain('check with the couple');
    });
  });

  it('shows not-found for unknown routes', async () => {
    const harness = await RouterTestingHarness.create('/nope');
    expect(harness.routeNativeElement?.textContent).toContain('Page not found');
  });
});
