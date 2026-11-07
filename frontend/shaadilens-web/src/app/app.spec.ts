import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';

describe('App routes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes, withComponentInputBinding()), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('redirects / to the admin dashboard', async () => {
    const harness = await RouterTestingHarness.create('/');
    expect(harness.routeNativeElement?.textContent).toContain('Dashboard');
  });

  it('shows the guest landing page with the link token', async () => {
    const harness = await RouterTestingHarness.create('/g/abc123');
    const text = harness.routeNativeElement?.textContent ?? '';
    expect(text).toContain('Find your photos');
    expect(text).toContain('abc123');
  });

  it('shows not-found for unknown routes', async () => {
    const harness = await RouterTestingHarness.create('/nope');
    expect(harness.routeNativeElement?.textContent).toContain('Page not found');
  });
});
