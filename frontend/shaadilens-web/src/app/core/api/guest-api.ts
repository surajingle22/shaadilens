import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MatchSummary, PagedResult, RegistrationFields, ShareLinkType } from './api.models';

export interface EventBranding {
  name: string;
  date: string | null;
  venue: string | null;
  themeColor: string;
  titleFont: string | null;
  coverUrl: string | null;
}

export interface PublicLink {
  event: EventBranding;
  linkType: ShareLinkType;
  requiresPin: boolean;
  requireRegistration: boolean;
  registrationFields: RegistrationFields;
  selfieRequired: boolean;
  faceSearchEnabled: boolean;
}

export interface GuestSessionInfo {
  event: EventBranding;
  linkType: ShareLinkType;
  guestName: string | null;
  hasSelfie: boolean;
  allowSingleDownload: boolean;
  allowBulkDownload: boolean;
  matchCount: number;
  probableCount: number;
  lastSearchedAt: string | null;
  selfieCount: number;
  maxSelfies: number;
  notifications: NotificationStatus;
}

/** Selection link state: "37 / 40", locked after submit. */
export interface SelectionState {
  limit: number | null;
  count: number;
  locked: boolean;
  submittedAt: string | null;
  items: { photoId: string; comment: string | null }[];
}

/** "Notify me" state. pushPublicKey = null → Web Push isn't set up on this server. */
export interface NotificationStatus {
  pushPublicKey: string | null;
  pushOn: boolean;
  emailAvailable: boolean;
  emailOn: boolean;
}

export interface GuestPhoto {
  id: string;
  collectionId: string;
  width: number;
  height: number;
  takenAt: string | null;
  thumbUrl: string;
  webUrl: string;
}

export interface GuestCollection {
  id: string;
  name: string;
  photoCount: number;
}

export interface GuestLogin {
  token: string;
  expiresAt: string;
  session: GuestSessionInfo;
  matches: MatchSummary | null;
}

export interface RegisterForm {
  name?: string;
  phone?: string;
  email?: string;
  consent: boolean;
  pinTicket?: string | null;
  selfie?: Blob | null;
}

export type ReelStatus = 'Queued' | 'Rendering' | 'Ready' | 'Failed' | 'Replaced';

export interface Reel {
  id: string;
  status: ReelStatus;
  photoCount: number;
  durationSeconds: number;
  sizeBytes: number;
  url: string | null;
  downloadUrl: string | null;
  createdAt: string;
  error: string | null;
}

/** Can the guest make a reel now (and why not), plus their latest reel. */
export interface ReelState {
  enabled: boolean;
  available: boolean;
  reason: string | null;
  photosAvailable: number;
  remainingToday: number;
  latest: Reel | null;
}

/** One bubble of the highlights strip: a collection's highlights. */
export interface HighlightStory {
  collectionId: string;
  name: string;
  photos: GuestPhoto[];
}

/** Guest endpoints. The guest JWT is added by the auth interceptor for /api/v1/public/*. */
@Injectable({ providedIn: 'root' })
export class GuestApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/public';

  link = (token: string) => firstValueFrom(this.http.get<PublicLink>(`${this.base}/links/${token}`));

  verifyPin = (token: string, pin: string) =>
    firstValueFrom(this.http.post<{ pinTicket: string }>(`${this.base}/links/${token}/verify-pin`, { pin }));

  register(token: string, f: RegisterForm): Promise<GuestLogin> {
    const body = new FormData();
    if (f.name) body.append('name', f.name);
    if (f.phone) body.append('phone', f.phone);
    if (f.email) body.append('email', f.email);
    body.append('consent', String(f.consent));
    if (f.pinTicket) body.append('pinTicket', f.pinTicket);
    if (f.selfie) body.append('selfie', f.selfie, 'selfie.jpg');
    return firstValueFrom(this.http.post<GuestLogin>(`${this.base}/links/${token}/register`, body));
  }

  me = () => firstValueFrom(this.http.get<GuestSessionInfo>(`${this.base}/me`));
  collections = () => firstValueFrom(this.http.get<GuestCollection[]>(`${this.base}/collections`));
  probable = () => firstValueFrom(this.http.get<GuestPhoto[]>(`${this.base}/me/probable`));
  /** "Find more photos of me": search again with all my selfies + confirmed faces. */
  findMore = () => firstValueFrom(this.http.post<MatchSummary>(`${this.base}/me/find-more`, {}));
  /** "Yes, that's me". */
  confirm = (photoId: string) => firstValueFrom(this.http.post<void>(`${this.base}/me/matches/${photoId}/confirm`, {}));
  /** "Not me" — never shown again. */
  reject = (photoId: string) => firstValueFrom(this.http.post<void>(`${this.base}/me/matches/${photoId}/reject`, {}));

  /** Extra selfie (another angle); the server searches again and returns the new totals. */
  addSelfie(selfie: Blob): Promise<MatchSummary> {
    const body = new FormData();
    body.append('selfie', selfie, 'selfie.jpg');
    return firstValueFrom(this.http.post<MatchSummary>(`${this.base}/me/selfie`, body));
  }
  deleteMe = () => firstValueFrom(this.http.delete<void>(`${this.base}/me`));

  highlights = () => firstValueFrom(this.http.get<HighlightStory[]>(`${this.base}/highlights`));
  reel = () => firstValueFrom(this.http.get<ReelState>(`${this.base}/me/reel`));
  makeReel = () => firstValueFrom(this.http.post<Reel>(`${this.base}/me/reel`, {}));

  // ---------- Selection links (album picks) ----------
  selection = () => firstValueFrom(this.http.get<SelectionState>(`${this.base}/selections`));
  select = (photoId: string) => firstValueFrom(this.http.post<SelectionState>(`${this.base}/selections/${photoId}`, {}));
  unselect = (photoId: string) => firstValueFrom(this.http.delete<SelectionState>(`${this.base}/selections/${photoId}`));
  comment = (photoId: string, comment: string | null) =>
    firstValueFrom(this.http.put<SelectionState>(`${this.base}/selections/${photoId}/comment`, { comment }));
  submitSelection = () => firstValueFrom(this.http.post<SelectionState>(`${this.base}/selections/submit`, {}));

  /** Turn on push (browser subscription) and/or email; both null/false = off. */
  setNotifications = (subscription: PushSubscriptionJSON | null, email: boolean) =>
    firstValueFrom(this.http.put<NotificationStatus>(`${this.base}/me/notifications`, { subscription, email }));
  notificationsOff = () => firstValueFrom(this.http.delete<NotificationStatus>(`${this.base}/me/notifications`));

  /** "Your photos" (Guest links) or the whole gallery (Full links). */
  photos(kind: 'mine' | 'gallery', collectionId: string | null, page: number, pageSize = 60) {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (collectionId) params = params.set('collectionId', collectionId);
    const url = kind === 'mine' ? `${this.base}/me/photos` : `${this.base}/gallery`;
    return firstValueFrom(this.http.get<PagedResult<GuestPhoto>>(url, { params }));
  }

  downloadUrl = (photoId: string) =>
    firstValueFrom(this.http.post<{ url: string }>(`${this.base}/photos/${photoId}/download`, {})).then((r) => r.url);

  downloadAllUrl = (collectionId: string | null) =>
    firstValueFrom(this.http.post<{ url: string }>(`${this.base}/me/download-all`, { collectionId })).then((r) => r.url);
}
