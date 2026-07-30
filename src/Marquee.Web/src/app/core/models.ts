export interface UserDto {
  id: string;
  username: string;
  email: string;
  bio: string | null;
  isPrivate: boolean;
  role: string;
}

export interface AuthResponse {
  token: string;
  user: UserDto;
}

export interface MovieDto {
  tmdbId: number;
  title: string;
  posterUrl: string | null;
  releaseYear: number | null;
  overview: string | null;
  voteAverage: number;
  voteCount: number;
}

export interface PremiereDto {
  id: string;
  scopeId: string;
  status: 'Scheduled' | 'Active' | 'Opened' | 'AutoOpened';
  scheduledFor: string;
  threshold: number;
  totalClaps: number;
  contributors: number;
  registeredClapCap: number;
  anonymousClapCap: number;
  opensAt: string | null;
  expiresAt: string | null;
  openedAt: string | null;
  myClaps: number;
  myCap: number;
  movie: MovieDto | null;
}

export interface ClapResponse {
  premiereId: string;
  status: string;
  totalClaps: number;
  threshold: number;
  myClaps: number;
  myCap: number;
  capReached: boolean;
  opened: boolean;
  movie: MovieDto | null;
}

/** Batched clap update from the hub — one message per Premiere per broadcast interval. */
export interface ClapUpdate {
  premiereId: string;
  totalClaps: number;
  threshold: number;
  contributors: number;
}

/** The reveal, pushed once when a Premiere opens. */
export interface PremiereOpenedNotification {
  premiereId: string;
  status: string;
  totalClaps: number;
  threshold: number;
  contributors: number;
  openedAt: string | null;
  movie: MovieDto | null;
}

/** A short-lived session that lets a visitor clap without an account (Iteration 5). */
export interface AnonymousSessionResponse {
  sessionId: string;
  token: string;
  expiresAtUtc: string;
}

export interface FriendContributorDto {
  userId: string;
  username: string;
}

export interface FriendContributorsResponse {
  premiereId: string;
  friends: FriendContributorDto[];
}

export interface LibraryEntryDto {
  movieId: string;
  movie: MovieDto;
  premiereId: string;
  acquiredAt: string;
  emblemTier: number | null;
}

/** Live operational numbers for the admin dashboard (Iteration 6). */
export interface AdminMetricsDto {
  queueDepth: number;
  /** False means the broker could not be reached — which is different news from a depth of zero. */
  queueDepthAvailable: boolean;
  deadLetterDepth: number;
  revealQueueDepth: number;
  activeConnections: number;
  clapsInWindow: number;
  clapRatePerMinute: number;
  rateWindowSeconds: number;
  activePremiereId: string | null;
  activePremiereThreshold: number | null;
  activePremiereExpiresAt: string | null;
  liveClaps: number;
  liveContributors: number;
  observedAt: string;
}
