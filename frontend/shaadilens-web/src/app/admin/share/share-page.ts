import { Clipboard } from '@angular/cdk/clipboard';
import { Component, effect, inject, resource, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AdminApi } from '../../core/api/admin-api';
import { API } from '../../core/api/api-urls';
import { SaveShareLinkRequest, ShareLink, ShareLinkType } from '../../core/api/api.models';
import { Notify } from '../../core/notify';
import { EventContext } from '../event/event-context';

@Component({
  selector: 'app-share-page',
  imports: [ReactiveFormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatMenuModule,
    MatProgressBarModule, MatSelectModule, MatSlideToggleModule, MatTooltipModule],
  templateUrl: './share-page.html',
  styleUrl: './share-page.scss',
})
export class SharePage {
  protected readonly ctx = inject(EventContext);
  private readonly api = inject(AdminApi);
  private readonly notify = inject(Notify);
  private readonly clipboard = inject(Clipboard);

  protected readonly urls = API;
  protected readonly links = resource({
    params: () => this.ctx.eventId(),
    loader: ({ params: id }) => this.api.shareLinks(id),
  });
  protected readonly creating = signal(false);
  /** PIN shown once right after generating it (only a hash is stored). */
  protected readonly revealedPin = signal<{ linkId: string; pin: string } | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    type: ['Guest' as ShareLinkType],
    label: [''],
    requireRegistration: [true],
    askName: [true],
    askPhone: [false],
    askEmail: [false],
    allowSingleDownload: [true],
    allowBulkDownload: [true],
    withPin: [false],
    selectionLimit: [null as number | null],
  });

  /** What each link type does — used for icons, names and the type picker. */
  protected readonly types: { value: ShareLinkType; icon: string; name: string; hint: string }[] = [
    { value: 'Guest', icon: 'face', name: 'Guest link', hint: 'Guests take a selfie and see only their photos' },
    { value: 'VipGuest', icon: 'star', name: 'VIP link', hint: 'Selfie only, no form (family, close friends)' },
    { value: 'Full', icon: 'photo_library', name: 'Full gallery link', hint: 'Everyone sees all photos' },
    { value: 'Selection', icon: 'favorite', name: 'Selection link', hint: 'The couple hearts photos for the album' },
  ];

  protected typeInfo(type: ShareLinkType) {
    return this.types.find((t) => t.value === type)!;
  }

  constructor() {
    // Open the create form automatically when there are no links yet.
    effect(() => {
      if (this.links.hasValue() && this.links.value().length === 0) this.creating.set(true);
    });
  }

  protected async create(): Promise<void> {
    const v = this.form.getRawValue();
    const fields = [v.askName && 'Name', v.askPhone && 'Phone', v.askEmail && 'Email'].filter(Boolean).join(', ') || 'None';
    try {
      const link = await this.api.createShareLink(this.ctx.eventId()!, {
        type: v.type, label: v.label || null, requireRegistration: v.requireRegistration, registrationFields: fields,
        allowSingleDownload: v.allowSingleDownload, allowBulkDownload: v.allowBulkDownload, expiresAt: null, isActive: true,
        selectionLimit: v.type === 'Selection' ? v.selectionLimit || null : null,
      });
      if (v.withPin) await this.pin(link, 'generate');
      this.notify.ok('Link created');
      this.creating.set(false);
      this.links.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async toggle(link: ShareLink, key: 'isActive' | 'allowSingleDownload' | 'allowBulkDownload' | 'requireRegistration', value: boolean): Promise<void> {
    const req: SaveShareLinkRequest = {
      type: link.type, label: link.label, requireRegistration: link.requireRegistration, registrationFields: link.registrationFields,
      allowSingleDownload: link.allowSingleDownload, allowBulkDownload: link.allowBulkDownload, expiresAt: link.expiresAt,
      isActive: link.isActive, selectionLimit: link.selectionLimit, [key]: value,
    };
    try {
      await this.api.updateShareLink(link.id, req);
      this.links.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async pin(link: ShareLink, action: 'generate' | 'remove'): Promise<void> {
    try {
      const res = await this.api.setPin(link.id, action);
      this.revealedPin.set(res.pin ? { linkId: link.id, pin: res.pin } : null);
      this.notify.ok(res.pin ? 'New PIN created — note it down, it’s shown only now' : 'PIN removed');
      this.links.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected async remove(link: ShareLink): Promise<void> {
    if (!confirm(`Delete this link? ${link.guestCount} guest(s) registered through it will lose access and their selfies will be deleted.`)) return;
    try {
      await this.api.deleteShareLink(link.id);
      this.links.reload();
    } catch (err) {
      this.notify.error(err);
    }
  }

  protected copy(text: string, what = 'Link'): void {
    this.clipboard.copy(text);
    this.notify.ok(`${what} copied`);
  }

  protected whatsapp(link: ShareLink): string {
    const name = this.ctx.event.value()?.name ?? 'our wedding';
    const text = link.type === 'Guest' || link.type === 'VipGuest'
      ? `Find your photos from ${name}! Open the link, take a selfie and see every photo you're in: ${link.url}`
      : `Photos from ${name}: ${link.url}`;
    return `https://wa.me/?text=${encodeURIComponent(text)}`;
  }

  protected fields(link: ShareLink): string {
    return !link.requireRegistration || link.registrationFields === 'None' ? 'no form' : link.registrationFields.toLowerCase();
  }
}
