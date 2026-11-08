import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { API } from './api-urls';
import {
  AppSettings,
  Collection,
  CompleteUploadResponse,
  EventDetail,
  EventStats,
  EventSummary,
  FaceSettings,
  GuestAdmin,
  GuestReview,
  MatchStatus,
  ThresholdPreview,
  InitUploadResponse,
  MatchSummary,
  PagedResult,
  Photo,
  PhotoDetail,
  PhotoStatus,
  PinResponse,
  SaveEventRequest,
  SaveShareLinkRequest,
  ShareLink,
  UpdatePhotoRequest,
  UploadStatusResponse,
  VapidKeys,
} from './api.models';

/**
 * Typed wrapper around the admin endpoints. Methods return Promises (async/await reads more like C#);
 * the admin auth cookie is sent automatically by the browser.
 */
@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly http = inject(HttpClient);

  // ---------- events ----------
  listEvents = () => this.get<EventSummary[]>(API.events);
  getEvent = (id: string) => this.get<EventDetail>(API.event(id));
  createEvent = (req: SaveEventRequest) => this.post<EventDetail>(API.events, req);
  updateEvent = (id: string, req: SaveEventRequest) => this.put<EventDetail>(API.event(id), req);
  deleteEvent = (id: string) => this.delete(API.event(id));
  publish = (id: string, isPublished: boolean) => this.post<void>(API.publish(id), { isPublished });
  stats = (id: string) => this.get<EventStats>(API.stats(id));
  saveFaceSettings = (id: string, s: FaceSettings) => this.put<void>(API.faceSettings(id), s);
  setCover = (id: string, photoId: string | null) => this.put<void>(API.cover(id), { photoId });

  // ---------- collections ----------
  addCollection = (eventId: string, name: string) => this.post<Collection>(API.collections(eventId), { name, isHidden: false });
  updateCollection = (eventId: string, c: Pick<Collection, 'id' | 'name' | 'isHidden'>) =>
    this.put<void>(API.collection(eventId, c.id), { name: c.name, isHidden: c.isHidden });
  deleteCollection = (eventId: string, id: string) => this.delete(API.collection(eventId, id));
  reorderCollections = (eventId: string, ids: string[]) => this.put<void>(API.collectionsOrder(eventId), ids);

  // ---------- photos ----------
  photos(eventId: string, q: { collectionId?: string | null; status?: PhotoStatus | null; page: number; pageSize: number }) {
    let params = new HttpParams().set('page', q.page).set('pageSize', q.pageSize);
    if (q.collectionId) params = params.set('collectionId', q.collectionId);
    if (q.status) params = params.set('status', q.status);
    return firstValueFrom(this.http.get<PagedResult<Photo>>(API.eventPhotos(eventId), { params }));
  }
  photo = (id: string) => this.get<PhotoDetail>(API.photo(id));
  updatePhoto = (id: string, req: UpdatePhotoRequest) => firstValueFrom(this.http.patch<void>(API.photo(id), req));
  deletePhoto = (id: string) => this.delete(API.photo(id));
  reprocess = (id: string) => this.post<void>(API.reprocess(id), {});
  retryFailed = (eventId: string) => this.post<{ queued: number }>(API.retryFailed(eventId), {});

  // ---------- uploads (used by UploadQueue) ----------
  initUpload = (eventId: string, collectionId: string, file: File) =>
    this.post<InitUploadResponse>(API.initUpload(eventId), { collectionId, fileName: file.name, size: file.size });
  uploadStatus = (uploadId: string) => this.get<UploadStatusResponse>(API.upload(uploadId));
  completeUpload = (uploadId: string) => this.post<CompleteUploadResponse>(API.completeUpload(uploadId), {});

  // ---------- share links ----------
  shareLinks = (eventId: string) => this.get<ShareLink[]>(API.shareLinks(eventId));
  createShareLink = (eventId: string, req: SaveShareLinkRequest) => this.post<ShareLink>(API.shareLinks(eventId), req);
  updateShareLink = (id: string, req: SaveShareLinkRequest) => this.put<ShareLink>(API.shareLink(id), req);
  deleteShareLink = (id: string) => this.delete(API.shareLink(id));
  setPin = (id: string, action: 'generate' | 'set' | 'remove', pin?: string) =>
    this.post<PinResponse>(API.shareLinkPin(id), { action, pin: pin ?? null });

  // ---------- guests ----------
  guests = (eventId: string) => this.get<GuestAdmin[]>(API.guests(eventId));
  deleteGuest = (id: string) => this.delete(API.guest(id));
  researchGuest = (id: string) => this.post<MatchSummary>(API.guestSearch(id), {});
  guestReview = (id: string) => this.get<GuestReview>(API.guestMatches(id));
  setMatchStatus = (matchId: string, status: Extract<MatchStatus, 'Confirmed' | 'Rejected'>) =>
    firstValueFrom(this.http.patch<void>(API.match(matchId), { status }));

  // ---------- thresholds ----------
  /** Compares every guest with every face (a few seconds on big events). */
  thresholdPreview = (eventId: string) => this.get<ThresholdPreview>(API.thresholdPreview(eventId));
  /** Search again for every guest in the background (apply new thresholds to everyone). */
  rematchAll = (eventId: string) => this.post<void>(API.rematch(eventId), {});
  /** Push/email the guest now ("resend notification"). */
  notifyGuest = (id: string) => this.post<void>(API.guestNotify(id), {});

  // ---------- settings ----------
  settings = () => this.get<AppSettings>(API.settings);
  testEmail = (to: string) => this.post<void>(API.testEmail, { to });
  vapidKeys = () => this.post<VapidKeys>(API.vapidKeys, {});

  // ---------- helpers ----------
  private get<T>(url: string) {
    return firstValueFrom(this.http.get<T>(url));
  }
  private post<T>(url: string, body: unknown) {
    return firstValueFrom(this.http.post<T>(url, body));
  }
  private put<T>(url: string, body: unknown) {
    return firstValueFrom(this.http.put<T>(url, body));
  }
  private delete(url: string) {
    return firstValueFrom(this.http.delete<void>(url));
  }
}
