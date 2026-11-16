import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { swBypassInterceptor } from './sw-bypass-interceptor';

describe('swBypassInterceptor', () => {
  it('asks the service worker to stay out of /api calls only', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([swBypassInterceptor])), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpClient);
    const ctrl = TestBed.inject(HttpTestingController);

    http.put('/api/v1/uploads/u1/chunks/0', new ArrayBuffer(3)).subscribe();
    http.get('/assets/config.json').subscribe();

    expect(ctrl.expectOne('/api/v1/uploads/u1/chunks/0').request.headers.get('ngsw-bypass')).toBe('true');
    expect(ctrl.expectOne('/assets/config.json').request.headers.has('ngsw-bypass')).toBe(false);
    ctrl.verify();
  });
});
