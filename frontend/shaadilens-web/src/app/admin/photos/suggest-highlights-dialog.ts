import { Component, inject, resource, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AdminApi } from '../../core/api/admin-api';
import { Notify } from '../../core/notify';

/**
 * "Suggest highlights": the server proposes sharp photos with people, nobody's eyes closed, one per burst, spread
 * over the collections (culling scores, Phase 3a). All are pre-selected; untick the ones you don't want.
 */
@Component({
  selector: 'app-suggest-highlights-dialog',
  imports: [MatButtonModule, MatDialogModule, MatIconModule, MatProgressBarModule],
  template: `
    <h2 mat-dialog-title>Suggested highlights</h2>
    <mat-dialog-content>
      <p class="muted">Sharp photos with people and everyone’s eyes open, the best of each burst, from every collection.
        Tap to untick. Guests see highlights as “stories” at the top of their gallery.</p>
      @if (suggestions.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="grid">
        @for (p of suggestions.value() ?? []; track p.id) {
          <button class="tile" [class.off]="!picked().has(p.id)" (click)="toggle(p.id)" [attr.aria-pressed]="picked().has(p.id)">
            <img [src]="p.thumbUrl" [alt]="p.fileName" loading="lazy" />
            <mat-icon class="check">{{ picked().has(p.id) ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
          </button>
        } @empty {
          @if (!suggestions.isLoading()) { <p class="muted">No suggestions — every good photo is already a highlight, or the photos haven’t been analysed yet.</p> }
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="!picked().size || busy()">
        <mat-icon>star</mat-icon> Mark {{ picked().size }} as highlights
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 6px; }
    .tile { position: relative; aspect-ratio: 1; padding: 0; border: 0; border-radius: 6px; overflow: hidden; cursor: pointer; }
    .tile img { width: 100%; height: 100%; object-fit: cover; display: block; transition: opacity .15s; }
    .tile.off img { opacity: .35; }
    .check { position: absolute; top: 4px; right: 4px; color: #fff; filter: drop-shadow(0 0 2px rgb(0 0 0 / .8)); }
  `,
})
export class SuggestHighlightsDialog {
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);
  private readonly ref = inject(MatDialogRef<SuggestHighlightsDialog, boolean>);
  private readonly eventId = inject<string>(MAT_DIALOG_DATA);

  protected readonly suggestions = resource({
    loader: async () => {
      const list = await this.api.highlightSuggestions(this.eventId);
      this.picked.set(new Set(list.map((p) => p.id)));
      return list;
    },
  });
  protected readonly picked = signal(new Set<string>());
  protected readonly busy = signal(false);

  protected toggle(id: string): void {
    this.picked.update((s) => {
      const next = new Set(s);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    try {
      const { changed } = await this.api.setHighlights(this.eventId, [...this.picked()], true);
      this.notify.ok(`${changed} photo(s) marked as highlights`);
      this.ref.close(true);
    } catch (err) {
      this.notify.error(err);
    } finally {
      this.busy.set(false);
    }
  }
}
