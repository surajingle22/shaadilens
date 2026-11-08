import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, input, resource, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../core/api/admin-api';
import { API } from '../../core/api/api-urls';
import { MatchReview as Match, MatchStatus } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { faceCropStyle } from '../../shared/face-crop';
import { EventContext } from '../event/event-context';

type Filter = 'all' | MatchStatus;

/**
 * Match Review for one guest: their selfie(s) next to every face the AI matched, with the similarity score.
 * Approve (✓ → Confirmed: also becomes a reference for "Find more") or reject (✗ → never shown again).
 * Lowest scores are the ones worth checking — "Maybe" matches come first.
 */
@Component({
  selector: 'app-match-review',
  imports: [DecimalPipe, MatButtonModule, MatButtonToggleModule, MatIconModule, MatProgressBarModule, MatTooltipModule, RouterLink],
  template: `
    <a mat-button routerLink=".."><mat-icon>arrow_back</mat-icon> All guests</a>

    @if (review.isLoading() && !review.hasValue()) { <mat-progress-bar mode="indeterminate" /> }

    @if (review.value(); as r) {
      <section class="panel head">
        <div class="selfies">
          @for (id of r.selfies; track id) {
            <img [src]="selfieUrl(id)" alt="Selfie" />
          } @empty {
            <div class="no-selfie"><mat-icon>person</mat-icon></div>
          }
        </div>
        <div>
          <h2>{{ r.guest.name ?? 'Guest' }}</h2>
          <p class="muted">
            {{ r.guest.matchCount }} photos · {{ r.guest.probableCount }} maybe · {{ r.guest.confirmedCount }} confirmed ·
            {{ r.guest.rejectedCount }} rejected · searches with {{ r.guest.referenceCount }} reference face(s)
          </p>
          <button mat-stroked-button (click)="research()" [disabled]="busy()"><mat-icon>person_search</mat-icon> Search again</button>
        </div>
      </section>

      <mat-button-toggle-group [value]="filter()" (change)="filter.set($event.value)" aria-label="Filter matches">
        <mat-button-toggle value="all">All · {{ r.matches.length }}</mat-button-toggle>
        @for (s of statuses; track s.value) {
          <mat-button-toggle [value]="s.value">{{ s.label }} · {{ count(s.value) }}</mat-button-toggle>
        }
      </mat-button-toggle-group>

      <div class="grid">
        @for (m of shown(); track m.id) {
          <figure class="card" [class]="m.status.toLowerCase()">
            <a class="face" [href]="m.webUrl" target="_blank" rel="noopener" [style]="faceStyle(m)" [matTooltip]="'Open the photo'"></a>
            <figcaption>
              <span class="score" [class.low]="m.status === 'Probable'">{{ m.score | number: '1.2-2' }}</span>
              <span class="status">{{ label(m.status) }}</span>
              <span class="spacer"></span>
              <button mat-icon-button (click)="set(m, 'Confirmed')" [disabled]="busy() || m.status === 'Confirmed'" matTooltip="Yes, it's them">
                <mat-icon>check</mat-icon></button>
              <button mat-icon-button (click)="set(m, 'Rejected')" [disabled]="busy() || m.status === 'Rejected'" matTooltip="Not them">
                <mat-icon>close</mat-icon></button>
            </figcaption>
          </figure>
        } @empty {
          <p class="muted">Nothing here.</p>
        }
      </div>
    }
  `,
  styles: `
    .head { display: flex; gap: 16px; align-items: center; flex-wrap: wrap; margin: 8px 0 16px; }
    .head h2 { margin: 0; }
    .head p { margin: 4px 0 8px; }
    .selfies { display: flex; gap: 8px; }
    .selfies img, .no-selfie { width: 96px; height: 96px; border-radius: 12px; object-fit: cover; background: var(--mat-sys-surface-container); }
    .no-selfie { display: grid; place-items: center; }
    mat-button-toggle-group { flex-wrap: wrap; margin-bottom: 12px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 10px; }
    .card { margin: 0; border-radius: 12px; overflow: hidden; background: var(--mat-sys-surface-container-low); border: 2px solid transparent; }
    .card.confirmed { border-color: #2e7d32; }
    .card.rejected { opacity: .5; }
    .card.probable { border-color: #ef6c00; }
    .face { display: block; aspect-ratio: 1; background-color: #222; }
    figcaption { display: flex; align-items: center; gap: 4px; padding: 0 2px 0 8px; }
    .score { font: var(--mat-sys-title-small); }
    .score.low { color: #ef6c00; }
    .status { font: var(--mat-sys-label-small); color: var(--mat-sys-on-surface-variant); }
    .spacer { flex: 1; }
  `,
})
export class MatchReview {
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);
  private readonly ctx = inject(EventContext);

  /** From the route: guests/:guestId */
  readonly guestId = input.required<string>();

  protected readonly review = resource({ params: () => this.guestId(), loader: ({ params: id }) => this.api.guestReview(id) });
  protected readonly filter = signal<Filter>('all');
  protected readonly busy = signal(false);
  protected readonly statuses: { value: MatchStatus; label: string }[] = [
    { value: 'Probable', label: 'Maybe' },
    { value: 'Auto', label: 'Auto' },
    { value: 'Confirmed', label: 'Confirmed' },
    { value: 'Rejected', label: 'Rejected' },
  ];
  protected readonly shown = computed(() => {
    const all = this.review.value()?.matches ?? [];
    const f = this.filter();
    return f === 'all' ? all : all.filter((m) => m.status === f);
  });

  protected count(s: MatchStatus): number {
    return (this.review.value()?.matches ?? []).filter((m) => m.status === s).length;
  }

  protected label(s: MatchStatus): string {
    return this.statuses.find((x) => x.value === s)!.label;
  }

  protected selfieUrl(referenceId: string): string {
    return API.selfieImage(referenceId);
  }

  /** Zoom the thumbnail onto the matched face (CSS only, see shared/face-crop.ts). */
  protected faceStyle(m: Match): Record<string, string> {
    return faceCropStyle(m.face, m.width, m.height, m.thumbUrl, m.webUrl);
  }

  protected async set(m: Match, status: 'Confirmed' | 'Rejected'): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.setMatchStatus(m.id, status);
      this.review.reload();
      this.ctx.stats.reload();
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }

  protected async research(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.researchGuest(this.guestId());
      this.notify.ok(`${r.auto} photos + ${r.probable} maybe`);
      this.review.reload();
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }
}
