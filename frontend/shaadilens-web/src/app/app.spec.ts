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
    await vi.waitFor(() => http.expectOne(API.me).flush(null, { status: 401, statusText: 'Unauthorized' }));
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

  it('shows the guest landing page with the link token', async () => {
    const harness = await RouterTestingHarness.create('/g/abc123');
    const text = harness.routeNativeElement?.textContent ?? '';
    expect(text).toContain('Find your photos');
  });

  it('shows not-found for unknown routes', async () => {
    const harness = await RouterTestingHarness.create('/nope');
    expect(harness.routeNativeElement?.textContent).toContain('Page not found');
  });
});
