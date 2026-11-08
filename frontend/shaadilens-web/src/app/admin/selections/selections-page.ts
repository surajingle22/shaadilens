import { Clipboard } from '@angular/cdk/clipboard';
import { DatePipe } from '@angular/common';
import { Component, inject, resource } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../core/api/admin-api';
import { API } from '../../core/api/api-urls';
import { SelectionLink } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { EventContext } from '../event/event-context';

/**
 * The couple's album picks (Selection links): thumbnails with their comments, plus exports:
 *  - file names as one line → paste into Lightroom (Library → Filter → Text → Filename → Contains)
 *  - CSV (names, collection, comments) and a ZIP of the originals
 */
@Component({
  selector: 'app-selections-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTooltipModule, RouterLink],
  template: `
    @if (links.isLoading() && !links.hasValue()) { <mat-progress-bar mode="indeterminate" /> }

    @for (l of links.value() ?? []; track l.linkId) {
      <section class="panel">
        <header>
          <div>
            <h2><mat-icon class="filled">favorite</mat-icon> {{ l.label || 'Selection link' }}</h2>
            <p class="muted small">
              {{ l.count }}{{ l.limit ? ' / ' + l.limit : '' }} picked ·
              @if (l.locked) { <b>submitted</b> {{ l.submittedAt | date: 'd MMM, HH:mm' }}{{ l.submittedBy ? ' by ' + l.submittedBy : '' }} }
              @else { still choosing }
            </p>
          </div>
          <div class="buttons">
            <button mat-stroked-button (click)="copyNames(l)" [disabled]="!l.count" matTooltip="One line for Lightroom's filename filter">
              <mat-icon>content_copy</mat-icon> Copy file names</button>
            <a mat-stroked-button [href]="exportUrl('csv', l)" [class.disabled]="!l.count"><mat-icon>table_view</mat-icon> CSV</a>
            <a mat-stroked-button [href]="exportUrl('zip', l)" [class.disabled]="!l.count"><mat-icon>folder_zip</mat-icon> ZIP</a>
            @if (l.locked) {
              <button mat-button (click)="unlock(l)"><mat-icon>lock_open</mat-icon> Unlock for changes</button>
            }
          </div>
        </header>

        <div class="grid">
          @for (i of l.items; track i.photoId) {
            <figure>
              <a [href]="i.webUrl" target="_blank" rel="noopener"><img [src]="i.thumbUrl" [alt]="i.fileName" loading="lazy" /></a>
              <figcaption>
                <span class="name" [title]="i.fileName">{{ i.fileName }}</span>
                @if (i.comment) { <span class="comment"><mat-icon inline>sticky_note_2</mat-icon> {{ i.comment }}</span> }
              </figcaption>
            </figure>
          } @empty {
            <p class="muted">Nothing picked yet.</p>
          }
        </div>
      </section>
    } @empty {
      @if (links.hasValue()) {
        <p class="muted">No Selection links yet. Create one on the <a routerLink="../share">Share</a> tab and send it to the couple.</p>
      }
    }
  `,
  styles: `
    header { display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; align-items: flex-start; }
    h2 { display: flex; align-items: center; gap: 8px; margin: 0; }
    h2 mat-icon { color: #e0245e; }
    .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .disabled { pointer-events: none; opacity: .5; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 10px; margin-top: 12px; }
    figure { margin: 0; }
    img { width: 100%; aspect-ratio: 1; object-fit: cover; border-radius: 8px; display: block; }
    figcaption { display: flex; flex-direction: column; gap: 2px; padding: 4px 2px; font: var(--mat-sys-body-small); }
    .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .comment { color: var(--mat-sys-on-surface-variant); }
    section { margin-bottom: 16px; }
  `,
})
export class SelectionsPage {
  private readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);
  private readonly clipboard = inject(Clipboard);

  protected readonly links = resource({ params: () => this.ctx.eventId(), loader: ({ params: id }) => this.api.selections(id) });

  protected exportUrl(format: 'csv' | 'zip', l: SelectionLink): string {
    return API.selectionExport(this.ctx.eventId()!, format, l.linkId);
  }

  protected async copyNames(l: SelectionLink): Promise<void> {
    try {
      this.clipboard.copy(await this.api.selectionNames(this.ctx.eventId()!, l.linkId));
      this.notify.ok('Copied. In Lightroom: Library → Filter → Text → Filename → Contains → paste.');
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async unlock(l: SelectionLink): Promise<void> {
    try {
      await this.api.unlockSelection(l.linkId);
      this.notify.ok('Unlocked — the couple can change their picks again.');
      this.links.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }
}
