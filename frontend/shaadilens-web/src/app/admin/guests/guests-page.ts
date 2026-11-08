import { DatePipe } from '@angular/common';
import { Component, computed, inject, resource } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../core/api/admin-api';
import { GuestAdmin } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { EventContext } from '../event/event-context';

@Component({
  selector: 'app-guests-page',
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatTableModule, MatTooltipModule, RouterLink],
  template: `
    @if (guests.isLoading() && !guests.hasValue()) { <mat-progress-bar mode="indeterminate" /> }

    @if (guests.value(); as list) {
      <p class="muted">{{ list.length }} guest(s) · {{ withSelfie() }} with a selfie</p>
      <div class="table-wrap panel">
        <table mat-table [dataSource]="list">
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef>Name</th>
            <td mat-cell *matCellDef="let g">{{ g.name ?? '—' }}</td>
          </ng-container>
          <ng-container matColumnDef="contact">
            <th mat-header-cell *matHeaderCellDef>Contact</th>
            <td mat-cell *matCellDef="let g">{{ g.phone ?? '' }} {{ g.email ?? '' }}</td>
          </ng-container>
          <ng-container matColumnDef="matches">
            <th mat-header-cell *matHeaderCellDef>Photos</th>
            <td mat-cell *matCellDef="let g">
              @if (g.hasSelfie) {
                {{ g.matchCount }} <span class="muted">+ {{ g.probableCount }} maybe</span>
                @if (g.rejectedCount) { <span class="muted"> · {{ g.rejectedCount }} not me</span> }
              } @else { <span class="muted">no selfie</span> }
            </td>
          </ng-container>
          <ng-container matColumnDef="created">
            <th mat-header-cell *matHeaderCellDef>Joined</th>
            <td mat-cell *matCellDef="let g">{{ g.createdAt | date: 'd MMM, HH:mm' }}</td>
          </ng-container>
          <ng-container matColumnDef="notify">
            <th mat-header-cell *matHeaderCellDef>Notify</th>
            <td mat-cell *matCellDef="let g">
              @if (g.pushOn || g.emailOn) {
                <span class="notify" [matTooltip]="g.lastNotifiedAt ? 'Last sent ' + (g.lastNotifiedAt | date: 'd MMM, HH:mm') : 'Nothing sent yet'">
                  @if (g.pushOn) { <mat-icon inline>notifications_active</mat-icon> }
                  @if (g.emailOn) { <mat-icon inline>mail</mat-icon> }
                </span>
              } @else { <span class="muted">—</span> }
            </td>
          </ng-container>
          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef></th>
            <td mat-cell *matCellDef="let g" class="actions">
              @if (g.hasSelfie) {
                <a mat-icon-button [routerLink]="[g.id]" matTooltip="Review matches"><mat-icon>fact_check</mat-icon></a>
                <button mat-icon-button (click)="research(g)" matTooltip="Search again (after new uploads)"><mat-icon>person_search</mat-icon></button>
              }
              @if (g.pushOn || g.emailOn) {
                <button mat-icon-button (click)="resend(g)" matTooltip="Send them a notification now"><mat-icon>send</mat-icon></button>
              }
              <button mat-icon-button (click)="remove(g)" matTooltip="Delete guest and selfie"><mat-icon>delete</mat-icon></button>
            </td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
        @if (!list.length) { <p class="muted empty">No guests yet. Share a Guest link from the Share tab.</p> }
      </div>
    }
  `,
  styles: `
    .table-wrap { padding: 0; overflow-x: auto; }
    table { width: 100%; background: transparent; }
    .actions { text-align: right; white-space: nowrap; }
    .empty { padding: 16px; }
    .notify { color: var(--mat-sys-primary); }
  `,
})
export class GuestsPage {
  private readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);

  protected readonly columns = ['name', 'contact', 'matches', 'notify', 'created', 'actions'];
  protected readonly guests = resource({
    params: () => this.ctx.eventId(),
    loader: ({ params: id }) => this.api.guests(id),
  });
  protected readonly withSelfie = computed(() => (this.guests.value() ?? []).filter((g) => g.hasSelfie).length);

  protected async research(g: GuestAdmin): Promise<void> {
    try {
      const r = await this.api.researchGuest(g.id);
      this.notify.ok(`${g.name ?? 'Guest'}: ${r.auto} photos + ${r.probable} maybe`);
      this.guests.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }


  protected async resend(g: GuestAdmin): Promise<void> {
    try {
      await this.api.notifyGuest(g.id);
      this.notify.ok(`Sending a notification to ${g.name ?? 'the guest'}…`);
      setTimeout(() => this.guests.reload(), 3000);
    } catch (err) {
      this.notify.error(err);
    }
  }
  protected async remove(g: GuestAdmin): Promise<void> {
    if (!confirm(`Delete ${g.name ?? 'this guest'}, their selfie and matches?`)) return;
    try {
      await this.api.deleteGuest(g.id);
      this.guests.reload();
      this.ctx.stats.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }
}
