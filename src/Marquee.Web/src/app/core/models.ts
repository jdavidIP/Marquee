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

/**
 * Missed means the Premiere's moment passed while the scheduler was not running, so it was retired
 * rather than started late. Unlike AutoOpened it never revealed its film.
 */
export type PremiereStatus = 'Scheduled' | 'Active' | 'Opened' | 'AutoOpened' | 'Missed';

export interface PremiereDto {
  id: string;
  scopeId: string;
  status: PremiereStatus;
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

/**
 * A search hit. Deliberately identical for public and private users — a private profile stays
 * discoverable, and these are the same fields a stranger may see anyway. The client must not use
 * isPrivate to decide what to display: the server already shaped the payload.
 */
export interface UserSearchResultDto {
  id: string;
  username: string;
  bio: string | null;
  isPrivate: boolean;
}

/** A pending friend request, in whichever direction it points. */
export interface FriendRequestDto {
  id: string;
  /** The other party — the addressee on an outgoing request, the requester on an incoming one. */
  userId: string;
  username: string;
  status: string;
  /** True when the signed-in user sent this request rather than received it. */
  outgoing: boolean;
  createdAt: string;
}

export interface FriendDto {
  userId: string;
  username: string;
  bio: string | null;
  isPrivate: boolean;
  friendsSince: string;
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

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AdminUserDto {
  id: string;
  username: string;
  email: string;
  role: string;
  isBlocked: boolean;
  isPrivate: boolean;
  createdAt: string;
  moviesCollected: number;
}

/**
 * A Premiere as an admin sees it. Unlike PremiereDto the movie is always present, even before the
 * reveal — an admin has to see what a Premiere is holding to decide whether to change it.
 */
export interface AdminPremiereDto {
  id: string;
  scopeId: string;
  status: PremiereStatus;
  scheduledFor: string;
  opensAt: string | null;
  expiresAt: string | null;
  openedAt: string | null;
  threshold: number;
  registeredClapCap: number;
  anonymousClapCap: number;
  totalClaps: number;
  contributors: number;
  movieId: string;
  movieTmdbId: number;
  movieTitle: string;
}

/** An inclusive range of local times, "HH:mm", a Premiere may be moved to. */
export interface ScheduleWindowDto {
  start: string;
  end: string;
}

/** What an admin may change about a Premiere — shown up front rather than discovered by rejection. */
export interface PremiereEditOptionsDto {
  premiereId: string;
  status: PremiereStatus;
  canEdit: boolean;
  localDate: string;
  allowedWindows: ScheduleWindowDto[];
  thresholdMin: number;
  thresholdMax: number;
  currentThreshold: number;
  registeredUsers: number;
}

/** An admin's narrowing for one re-roll. Every field only narrows; §4.6's floors always apply. */
export interface MovieFilterRequest {
  minVoteAverage?: number | null;
  minYear?: number | null;
  maxYear?: number | null;
  genreId?: number | null;
  originalLanguage?: string | null;
  minRuntime?: number | null;
  maxRuntime?: number | null;
  originCountry?: string | null;
}

export interface MovieSearchResultDto {
  tmdbId: number;
  title: string;
  originalTitle: string | null;
  posterUrl: string | null;
  releaseYear: number | null;
  overview: string | null;
  voteAverage: number;
  voteCount: number;
  /** When this film was last revealed, or null if it never has been. */
  lastPremieredAt: string | null;
  /** When it becomes freely selectable again. */
  eligibleFrom: string | null;
  /** Still resting — pickable, but only with an explicit acknowledgement. */
  inCooldown: boolean;
  /** Already lined up for a Premiere that has not run. Not pickable at all. */
  alreadyQueued: boolean;
}

export interface GenreDto {
  tmdbId: number;
  name: string;
}

export interface CountryDto {
  iso3166Code: string;
  name: string;
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
