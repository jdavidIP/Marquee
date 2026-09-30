import { TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';
import { HubConnectionBuilder, HubConnectionState, RetryContext } from '@microsoft/signalr';
import { AuthService } from './auth.service';
import { RealtimeService, reconnectForever } from './realtime.service';

/**
 * The reconnect policy (#105). SignalR's default gives up after four attempts and nothing reopens
 * the socket, so these pin the two properties that fix that: it never stops, and it never waits
 * longer than the cap.
 */
describe('reconnectForever', () => {
  const delay = (previousRetryCount: number) =>
    reconnectForever.nextRetryDelayInMilliseconds({
      previousRetryCount,
      elapsedMilliseconds: 0,
      retryReason: new Error('connection lost'),
    } as RetryContext);

  it('backs off 1, 2, 4, 8 s before reaching the cap, within its jitter', () => {
    spyOn(Math, 'random').and.returnValue(0);
    expect([0, 1, 2, 3].map(delay)).toEqual([1_000, 2_000, 4_000, 8_000]);
  });

  it('caps the wait at 10 s plus at most 1 s of jitter, however many attempts have failed', () => {
    for (const count of [4, 10, 100, 10_000]) {
      const d = delay(count)!;
      expect(d).toBeGreaterThanOrEqual(10_000);
      expect(d).toBeLessThan(11_000);
    }
  });

  it('never returns null, so SignalR never stops retrying', () => {
    for (let count = 0; count < 500; count++) {
      expect(delay(count)).not.toBeNull();
    }
  });

  it('adds jitter so clients that dropped together do not retry in step', () => {
    spyOn(Math, 'random').and.returnValue(0.5);
    expect(delay(0)).toBe(1_500);
    expect(delay(20)).toBe(10_500);
  });
});

/**
 * A failed first start (#105 review). SignalR's automatic reconnect only covers a connection that
 * was established once, so if the API is down at page load (a deploy, say) the service has to
 * retry the start itself — otherwise the page stays on polling until refreshed.
 */
describe('RealtimeService first connection', () => {
  function fakeConnection(startOutcomes: boolean[]) {
    const fake = {
      state: HubConnectionState.Disconnected,
      starts: 0,
      start: jasmine.createSpy('start').and.callFake(() => {
        const ok = startOutcomes[fake.starts++] ?? true;
        if (!ok) return Promise.reject(new Error('negotiate failed'));
        fake.state = HubConnectionState.Connected;
        return Promise.resolve();
      }),
      invoke: jasmine.createSpy('invoke').and.returnValue(Promise.resolve()),
      on: () => {},
      onreconnected: () => {},
      onreconnecting: () => {},
      onclose: () => {},
    };
    spyOn(HubConnectionBuilder.prototype, 'build').and.returnValue(fake as never);
    return fake;
  }

  function makeService() {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: AuthService, useValue: { token: null } }] });
    return TestBed.inject(RealtimeService);
  }

  beforeEach(() => spyOn(Math, 'random').and.returnValue(0));

  it('retries a failed first start on the backoff until it connects', fakeAsync(() => {
    const fake = fakeConnection([false, false, true]);
    const service = makeService();

    void service.connect();
    flushMicrotasks();
    expect(fake.start).toHaveBeenCalledTimes(1);
    expect(service.connected()).toBeFalse();

    tick(1_000); // first retry after the base delay
    expect(fake.start).toHaveBeenCalledTimes(2);
    expect(service.connected()).toBeFalse();

    tick(2_000); // then double
    expect(fake.start).toHaveBeenCalledTimes(3);
    expect(service.connected()).toBeTrue();
  }));

  it('tells the page to catch up when the first connection only succeeded on a retry', fakeAsync(() => {
    fakeConnection([false, true]);
    const service = makeService();
    let caughtUp = 0;
    service.reconnected.subscribe(() => caughtUp++);

    void service.connect();
    flushMicrotasks();
    tick(1_000);

    expect(caughtUp).toBe(1);
  }));

  it('neither retries nor asks for a catch-up when the first start succeeds', fakeAsync(() => {
    const fake = fakeConnection([true]);
    const service = makeService();
    let caughtUp = 0;
    service.reconnected.subscribe(() => caughtUp++);

    void service.connect();
    flushMicrotasks();
    tick(60_000);

    expect(fake.start).toHaveBeenCalledTimes(1);
    expect(caughtUp).toBe(0);
  }));

  it('keeps a single retry pending even if connect() is called again meanwhile', fakeAsync(() => {
    const fake = fakeConnection([false, false, true]);
    const service = makeService();

    void service.connect();
    flushMicrotasks();
    void service.connect(); // e.g. watchPremiere while the first retry is pending: fails, no second timer
    flushMicrotasks();
    expect(fake.start).toHaveBeenCalledTimes(2);

    tick(1_000);
    expect(fake.start).toHaveBeenCalledTimes(3);
    expect(service.connected()).toBeTrue();

    // Once connected, a leftover second timer would show up here as another start attempt.
    fake.state = HubConnectionState.Disconnected;
    tick(60_000);
    expect(fake.start).toHaveBeenCalledTimes(3);
  }));
});
