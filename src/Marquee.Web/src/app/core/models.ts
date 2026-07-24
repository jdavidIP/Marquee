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
  threshold: number;
  totalClaps: number;
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

export interface LibraryEntryDto {
  movieId: string;
  movie: MovieDto;
  premiereId: string;
  acquiredAt: string;
  emblemTier: number | null;
}
