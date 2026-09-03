export interface UserDto {
  id: string;
  username: string;
  email: string;
  bio: string | null;
  isPrivate: boolean;
  role: string;
  /** Null for anyone who has not set a picture — a monogram of the username stands in. */
  avatarUrl: string | null;
}

export interface AuthResponse {
  token: string;
  user: UserDto;
}

/**
 * What the server will accept in a password (issue #27), read from GET /api/auth/password-rules
 * rather than restated here. The thresholds live in the API's options, so a copy in the client
 * would start lying the moment one was retuned.
 */
export interface PasswordRulesDto {
  minLength: number;
  maxLength: number;
  requireLetter: boolean;
  requireDigit: boolean;
}

/** One rejected password rule, so the form can list the reasons instead of running them together. */
export interface PasswordProblemDto {
  rule: string;
  message: string;
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

/** A profile as seen by someone entitled to all of it: the owner, an admin, a friend, or anyone
 *  at all when the account is public. */
export interface FullProfileDto {
  id: string;
  username: string;
  bio: string | null;
  /** Null for anyone who has not set a picture — a monogram of the username stands in. */
  avatarUrl: string | null;
  isPrivate: boolean;
  createdAt: string;
  moviesCollected: number;
  premieresAttended: number;
  friendCount: number;
  /** 'Pending' | 'Accepted' | null — the viewer's own relationship to this profile. */
  friendshipStatus: string | null;
  /** True when the viewer sent the pending request, false when they received it. */
  friendRequestOutgoing: boolean | null;
}

/**
 * A private profile seen by a stranger. The account's own fields — counts, join date, isPrivate
 * itself — are **absent from the payload, not null**: a null still tells the reader the field
 * exists and leaks its shape, an absent one says nothing at all. That is why this is a separate
 * type rather than a nulled-out FullProfileDto.
 *
 * friendshipStatus and friendRequestOutgoing are the one exception. They describe the *viewer's
 * own relationship* to the account, not the account's own detail, so withholding them was never
 * required by the privacy rule — and withholding them in practice meant a stranger's Add Friend
 * button had nothing to go on, forced to discover "already pending" by rejection. status is never
 * 'Accepted' here: an accepted friend is entitled to the full profile instead.
 */
export interface LimitedProfileDto {
  username: string;
  bio: string | null;
  /** Kept even on a restricted profile: a picture is part of the public identity, like the name. */
  avatarUrl: string | null;
  friendshipStatus: string | null;
  friendRequestOutgoing: boolean | null;
}

export type ProfileDto = FullProfileDto | LimitedProfileDto;

/**
 * Which shape came back.
 *
 * Keyed on a field only the full payload carries — never on isPrivate. The server has already
 * decided what this viewer is entitled to, and a friend sees everything even when the account is
 * private. Re-deriving that decision here would be a second copy of a privacy rule that must not
 * drift from the one in UserProfileService.
 */
export function isFullProfile(profile: ProfileDto): profile is FullProfileDto {
  return 'id' in profile;
}

/** Partial update of your own profile. Both optional, so one can change without clobbering the other. */
export interface UpdateProfileRequest {
  bio?: string | null;
  isPrivate?: boolean | null;
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

/** One face in the Premiere crowd/lobby strip. */
export interface LobbyFaceDto {
  userId: string;
  username: string;
  avatarUrl: string | null;
  isFriend: boolean;
}

/**
 * The crowd/lobby strip's data for the caller. `faces` is empty for an anonymous viewer — draw
 * `min(9, registeredCount)` faceless discs instead of leaving the strip blank.
 */
export interface LobbyDto {
  premiereId: string;
  faces: LobbyFaceDto[];
  registeredCount: number;
  anonymousCount: number;
}

export interface LibraryEntryDto {
  movieId: string;
  movie: MovieDto;
  premiereId: string;
  acquiredAt: string;
  emblemTier: number | null;
}

/** What a library listing can be ordered by. Mirrors the API's LibrarySort enum by name. */
export type LibrarySort = 'Acquired' | 'Title' | 'ReleaseYear' | 'Rating';

/**
 * One request for a slice of a library. Every field is optional; an empty query is the whole
 * library, most recently acquired first.
 *
 * `desc` left undefined means "however this field normally reads" — the server decides, so the
 * default direction is not duplicated here where it could drift.
 */
export interface LibraryQuery {
  search?: string;
  genreId?: number | null;
  minYear?: number | null;
  maxYear?: number | null;
  sort?: LibrarySort;
  desc?: boolean | null;
  page?: number;
  pageSize?: number;
}

/**
 * The filter values worth offering for one library — the genres it actually contains and the years
 * it actually spans. Comes from the API rather than a client-side constant so the controls cannot
 * drift from the seeded reference tables, and so no filter is offered that would return nothing.
 */
export interface LibraryFiltersDto {
  genres: GenreDto[];
  minYear: number | null;
  maxYear: number | null;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** What a premiere history listing can be ordered by. Mirrors the API's PremiereHistorySort enum. */
export type PremiereHistorySort = 'Opened' | 'Title' | 'Rating';

/**
 * One request for a slice of a premiere history. No genre/year filter here, unlike LibraryQuery —
 * this is a record of when the viewer attended, not a second way to browse movies by their own
 * attributes.
 */
export interface PremiereHistoryQuery {
  search?: string;
  sort?: PremiereHistorySort;
  desc?: boolean | null;
  page?: number;
  pageSize?: number;
}

/**
 * One Premiere a user contributed to. Keyed on the Premiere, not the movie — unlike a
 * LibraryEntryDto, a film re-premiered and attended twice appears here twice (issue #37 is the
 * reason the two must not be conflated).
 */
export interface PremiereHistoryEntryDto {
  premiereId: string;
  movie: MovieDto;
  openedAt: string | null;
  status: string;
  clapCount: number;
  emblemTier: number | null;
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
