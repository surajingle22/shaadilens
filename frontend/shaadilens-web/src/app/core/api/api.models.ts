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
  /** When face data was deleted after expiry (null = not purged). */
  purgedAt: string | null;
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
  // Culling report (Phase 3)
  analysed: boolean;
  isBlurry: boolean;
  closedEyes: number;
  burstGroupId: string | null;
  isBurstBest: boolean;
}

export type PhotoFilter = 'All' | 'Blurry' | 'EyesClosed' | 'Duplicates' | 'Highlights' | 'Hidden';

/** Counts for the culling filter chips. burstExtras = photos that could be hidden (each burst minus its keeper). */
export interface CullingSummary {
  processed: number;
  notAnalysed: number;
  blurry: number;
  eyesClosed: number;
  burstGroups: number;
  burstExtras: number;
  highlights: number;
  hidden: number;
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
  eyeAspectRatio: number | null;
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
  /** Selection links: max hearts (null = unlimited). */
  selectionLimit: number | null;
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
  selectionLimit?: number | null;
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
  pushOn: boolean;
  emailOn: boolean;
  lastNotifiedAt: string | null;
}

/** The couple's album picks through one Selection link. */
export interface SelectionLink {
  linkId: string;
  label: string | null;
  limit: number | null;
  count: number;
  locked: boolean;
  submittedAt: string | null;
  submittedBy: string | null;
  items: {
    photoId: string;
    fileName: string;
    collection: string;
    thumbUrl: string;
    webUrl: string;
    comment: string | null;
    selectedBy: string | null;
    createdAt: string;
  }[];
}

/** Admin → Settings (read-only; secrets live in .env). */
export interface AppSettings {
  publicBaseUrl: string;
  storageRoot: string;
  faceServiceUrl: string;
  faceWorkers: number;
  imageWorkers: number;
  smtp: { configured: boolean; host: string | null; port: number; from: string | null };
  push: { configured: boolean; publicKey: string | null; subject: string };
}

export interface VapidKeys {
  publicKey: string;
  privateKey: string;
  envLines: string;
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

/** Admin → People: faces grouped by clustering. */
export interface Person {
  id: string;
  number: number;
  name: string | null;
  guestId: string | null;
  guestName: string | null;
  faceCount: number;
  photoCount: number;
  samples: { photoId: string; thumbUrl: string; webUrl: string; width: number; height: number; face: { x: number; y: number; w: number; h: number } }[];
}

export interface People {
  updatedAt: string | null;
  running: boolean;
  faces: number;
  groupedFaces: number;
  people: Person[];
}
