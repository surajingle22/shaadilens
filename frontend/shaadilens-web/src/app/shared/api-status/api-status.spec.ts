import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API } from '../../core/api/api-urls';
import { ApiStatus } from './api-status';

describe('ApiStatus', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ApiStatus],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows API connected and each dependency check', async () => {
    const fixture = TestBed.createComponent(ApiStatus);
    fixture.detectChanges();
    TestBed.tick();

    http.expectOne(API.ping).flush({ name: 'ShaadiLens API', version: '1.0.0', environment: 'Development', serverTimeUtc: '' });
    http.expectOne(API.health).flush({
      status: 'Healthy',
      totalMs: 3,
      checks: { postgres: { status: 'Healthy', durationMs: 1 }, 'face-service': { status: 'Healthy', durationMs: 2 } },
    });
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('API connected');
    expect(text).toContain('postgres: Healthy');
    expect(text).toContain('face-service: Healthy');
  });

  it('still lists checks when /health returns 503', async () => {
    const fixture = TestBed.createComponent(ApiStatus);
    fixture.detectChanges();
    TestBed.tick();

    http.expectOne(API.ping).flush({ name: 'x', version: '1', environment: 'Development', serverTimeUtc: '' });
    http.expectOne(API.health).flush(
      { status: 'Unhealthy', totalMs: 3, checks: { 'face-service': { status: 'Unhealthy', durationMs: 2, description: 'still loading' } } },
      { status: 503, statusText: 'Service Unavailable' },
    );
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('face-service: Unhealthy');
  });
});
