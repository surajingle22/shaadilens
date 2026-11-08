// TypeScript mirrors of the API's DTOs (ShaadiLens.Application.*). Enums arrive as strings.

/** Mirrors ShaadiLens.Api.Controllers.PingResponse */
export interface PingResponse {
  name: string;
  version: string;
  environment: string;
  serverTimeUtc: string;
}

/** Shape written by the API's HealthResponseWriter at GET /health */
export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  totalMs: number;
  checks: Record<string, { status: string; durationMs: number; description?: string | null }>;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

// ---------- admin ----------

export interface AdminSession {
  email: string;
  expiresAt: string | null;
}

export interface FaceSettings {
  matchThreshold: number;
  probableThreshold: number;
  minFaceSizePx: number;
  minDetScore: number;
  maxSelfieYaw: number;
}

export interface Collection {
  id: string;
  name: string;
  sortOrder: number;
  isHidden: boolean;
  photoCount: number;
}

export interface EventSummary {
  id: string;
  name: string;
  slug: string;
  date: string | null;
  venue: string | null;
  isPublished: boolean;
  photoCount: number;
  guestCount: number;
  createdAt: string;
}

export interface EventDetail {
  id: string;
  name: string;
  slug: string;
  date: string | null;
  venue: string | null;
  themeColor: string;
  titleFont: string | null;
  watermarkEnabled: boolean;
  watermarkText: string | null;
  isPublished: boolean;
  faceSearchEnabled: boolean;
  coverPhotoId: string | null;
  coverUrl: string | null;
  expiresAt: string | null;
  createdAt: string;
  faceSettings: FaceSettings;
  collections: Collection[];
}

export interface SaveEventRequest {
  name: string;
  date: string | null;
  venue: string | null;
  themeColor: string | null;
  titleFont: string | null;
  watermarkEnabled: boolean;
  watermarkText: string | null;
  faceSearchEnabled: boolean;
  expiresAt: string | null;
}

export type PhotoStatus = 'Uploaded' | 'Resized' | 'FacesDetected' | 'Matched' | 'Failed';

export interface EventStats {
  photos: number;
  processed: number;
  pending: number;
  failed: number;
  faces: number;
  guests: number;
  matches: number;
  byStatus: Partial<Record<PhotoStatus, number>>;
}

export interface Photo {
  id: string;
  collectionId: string;
  fileName: string;
  width: number;
  height: number;
  takenAt: string | null;
  status: PhotoStatus;
  faceCount: number;
  isHighlight: boolean;
  isHidden: boolean;
  uploadedAt: string;
  error: string | null;
  thumbUrl: string | null;
  webUrl: string | null;
}

export interface FaceBox {
  id: string;
  x: number;
  y: number;
  w: number;
  h: number;
  detScore: number;
  faceSizePx: number;
  yaw: number;
  quality: number;
}

export interface PhotoDetail {
  photo: Photo;
  cameraModel: string | null;
  faces: FaceBox[];
}

export interface UpdatePhotoRequest {
  isHighlight?: boolean;
  isHidden?: boolean;
  collectionId?: string;
}

// ---------- uploads ----------

export interface InitUploadResponse {
  uploadId: string;
  chunkSize: number;
  totalChunks: number;
}

export interface UploadStatusResponse {
  uploadId: string;
  totalChunks: number;
  receivedChunks: number[];
  photoId: string | null;
}

export interface CompleteUploadResponse {
  status: 'created' | 'duplicate';
  photoId: string;
}

// ---------- sharing ----------

export type ShareLinkType = 'Guest' | 'VipGuest' | 'Full' | 'Selection';

/** [Flags] enum serialised as a comma-separated string, e.g. "Name, Phone" or "None". */
export type RegistrationFields = string;

export interface ShareLink {
  id: string;
  type: ShareLinkType;
  label: string | null;
  token: string;
  url: string;
  hasPin: boolean;
  requireRegistration: boolean;
  registrationFields: RegistrationFields;
  allowSingleDownload: boolean;
  allowBulkDownload: boolean;
  expiresAt: string | null;
  isActive: boolean;
  guestCount: number;
  createdAt: string;
}

export interface SaveShareLinkRequest {
  type: ShareLinkType;
  label: string | null;
  requireRegistration: boolean;
  registrationFields: RegistrationFields;
  allowSingleDownload: boolean;
  allowBulkDownload: boolean;
  expiresAt: string | null;
  isActive: boolean;
}

export interface PinResponse {
  hasPin: boolean;
  pin: string | null;
}

export interface GuestAdmin {
  id: string;
  name: string | null;
  phone: string | null;
  email: string | null;
  shareLinkId: string;
  hasSelfie: boolean;
  matchCount: number;
  probableCount: number;
  confirmedCount: number;
  rejectedCount: number;
  /** Selfies + confirmed faces used for searching. */
  referenceCount: number;
  createdAt: string;
  lastSearchedAt: string | null;
}

export type MatchStatus = 'Auto' | 'Probable' | 'Confirmed' | 'Rejected';

/** One matched face, for Match Review. `face` is normalised 0..1. */
export interface MatchReview {
  id: string;
  photoId: string;
  thumbUrl: string;
  webUrl: string;
  width: number;
  height: number;
  face: { x: number; y: number; w: number; h: number };
  score: number;
  status: MatchStatus;
  createdAt: string;
}

export interface GuestReview {
  guest: GuestAdmin;
  /** Reference ids with a selfie image (see API.selfieImage). */
  selfies: string[];
  matches: MatchReview[];
}

/** "At 0.40: 312 matches". Buckets are 0.01 wide (score = lower edge). */
export interface ThresholdPreview {
  threshold: number;
  matches: number;
  confirmed: number;
  rejected: number;
  guests: number;
  minScore: number;
  buckets: { score: number; count: number }[];
  elapsedMs: number;
}

export interface MatchSummary {
  auto: number;
  probable: number;
  facesCompared: number;
  elapsedMs: number;
}

/** RFC 7807 problem details returned by the API on errors. */
export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
  code?: string;
}
