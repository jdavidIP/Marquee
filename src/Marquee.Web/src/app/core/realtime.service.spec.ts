import { RetryContext } from '@microsoft/signalr';
import { reconnectForever } from './realtime.service';

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
