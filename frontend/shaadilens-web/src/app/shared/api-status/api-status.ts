import { httpResource } from '@angular/common/http';
import { Component, computed } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { API } from '../../core/api/api-urls';
import { HealthReport, PingResponse } from '../../core/api/api.models';

/**
 * Shows whether the browser can reach the API and whether the API's dependencies
 * (Postgres, face-service) are healthy. httpResource = signal-based HTTP GET:
 * .value(), .isLoading(), .error() are signals the template reads directly.
 */
@Component({
  selector: 'app-api-status',
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './api-status.html',
  styleUrl: './api-status.scss',
})
export class ApiStatus {
  protected readonly ping = httpResource<PingResponse>(() => API.ping);
  // /health returns 503 + the same JSON body when unhealthy; we still want the body.
  protected readonly health = httpResource<HealthReport>(() => API.health);

  protected readonly checks = computed(() => {
    // Reading .value() while the resource is in an error state throws, so check first.
    const report = this.health.hasValue() ? this.health.value() : this.unhealthyBody();
    return report ? Object.entries(report.checks).map(([name, c]) => ({ name, ...c })) : [];
  });

  protected reload(): void {
    this.ping.reload();
    this.health.reload();
  }

  private unhealthyBody(): HealthReport | undefined {
    const err = this.health.error() as { error?: HealthReport } | undefined;
    return err?.error?.checks ? err.error : undefined;
  }
}
