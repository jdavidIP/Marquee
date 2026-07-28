import { Injectable, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthService } from './auth.service';
import { ClapUpdate, PremiereDto, PremiereOpenedNotification } from './models';

/**
 * The live Premiere feed. One hub connection for the app, shared by whoever is listening.
 *
 * Group membership is derived from the scope, never hardcoded to a single global broadcast, so a
 * scoped Premiere later is a different scopeId rather than a change here.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);

  private connection: HubConnection | null = null;
  private joinedPremiereId: string | null = null;

  /** Drives the "live" indicator; also tells the page whether it needs its polling fallback. */
  readonly connected = signal(false);

  readonly clapUpdates = new Subject<ClapUpdate>();
  readonly premiereOpened = new Subject<PremiereOpenedNotification>();
  readonly premiereActivated = new Subject<PremiereDto>();

  /** Idempotent: repeated calls reuse the existing connection. */
  async connect(): Promise<void> {
    if (this.connection) {
      await this.started();
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl(environment.hubUrl, {
        // Watching is public, so an anonymous visitor connects without a token.
        accessTokenFactory: () => this.auth.token ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('clapUpdate', (u: ClapUpdate) => this.clapUpdates.next(u));
    connection.on('premiereOpened', (n: PremiereOpenedNotification) => this.premiereOpened.next(n));
    connection.on('premiereActivated', (p: PremiereDto) => this.premiereActivated.next(p));

    connection.onreconnected(async () => {
      this.connected.set(true);
      // Group membership does not survive a reconnect — the server sees a new connection id.
      await this.rejoin();
    });
    connection.onreconnecting(() => this.connected.set(false));
    connection.onclose(() => this.connected.set(false));

    this.connection = connection;
    await this.started();
  }

  /** Watch one Premiere. Leaves the previous one so a client never accumulates groups. */
  async watchPremiere(premiereId: string): Promise<void> {
    if (this.joinedPremiereId === premiereId) return;

    await this.connect();
    if (this.joinedPremiereId) {
      await this.invokeQuietly('LeavePremiere', environment.scopeId, this.joinedPremiereId);
    }
    this.joinedPremiereId = premiereId;
    await this.invokeQuietly('JoinPremiere', environment.scopeId, premiereId);
  }

  async stopWatching(): Promise<void> {
    if (!this.joinedPremiereId) return;
    const id = this.joinedPremiereId;
    this.joinedPremiereId = null;
    await this.invokeQuietly('LeavePremiere', environment.scopeId, id);
  }

  private async started(): Promise<void> {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Disconnected) return;

    try {
      await connection.start();
      this.connected.set(true);
      await this.rejoin();
    } catch {
      // The page falls back to polling; automatic reconnect keeps trying in the background.
      this.connected.set(false);
    }
  }

  private async rejoin(): Promise<void> {
    await this.invokeQuietly('JoinScope', environment.scopeId);
    if (this.joinedPremiereId) {
      await this.invokeQuietly('JoinPremiere', environment.scopeId, this.joinedPremiereId);
    }
  }

  private async invokeQuietly(method: string, ...args: unknown[]): Promise<void> {
    if (this.connection?.state !== HubConnectionState.Connected) return;
    try {
      await this.connection.invoke(method, ...args);
    } catch {
      // A failed join is recoverable — onreconnected re-joins, and the fallback poll covers the gap.
    }
  }
}
