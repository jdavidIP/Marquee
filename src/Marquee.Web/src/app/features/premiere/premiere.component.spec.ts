import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError, Subject } from 'rxjs';
import { PremiereComponent } from './premiere.component';
import { PremiereService } from '../../core/premiere.service';
import { RealtimeService } from '../../core/realtime.service';
import { AuthService } from '../../core/auth.service';
import { AnonymousSessionService } from '../../core/anonymous-session.service';
import { LobbyDto, PremiereDto, TodayScheduleDto, TodayScheduleSlotDto } from '../../core/models';

/**
 * The redesigned Premiere screen's derived state (issue #32, "Neon & chrome, 1958"). No DOM
 * assertions here — the live/reveal states themselves were checked by hand against the design
 * reference in the browser. This locks in the arithmetic and copy a later change could silently
 * break: the curtain/bulb formulas, and the crowd note's singular/plural branches — "1 people
 * opened this Premiere together" was a real bug caught live before this test existed.
 */
describe('PremiereComponent', () => {
  function premiere(overrides: Partial<PremiereDto> = {}): PremiereDto {
    return {
      id: 'p1',
      scopeId: 'global',
      status: 'Active',
      scheduledFor: '2026-01-01T00:00:00Z',
      threshold: 100,
      totalClaps: 0,
      contributors: 0,
      registeredClapCap: 6,
      anonymousClapCap: 2,
      opensAt: '2026-01-01T00:00:00Z',
      expiresAt: '2026-01-01T01:00:00Z',
      openedAt: null,
      myClaps: 0,
      myCap: 6,
      movie: null,
      myEmblemTier: null,
      ...overrides,
    };
  }

  function lobby(overrides: Partial<LobbyDto> = {}): LobbyDto {
    return { premiereId: 'p1', faces: [], registeredCount: 0, anonymousCount: 0, ...overrides };
  }

  let slotAutoId = 0;

  /**
   * Defaults a movie even for a Scheduled slot: the backend withholding it until reveal is
   * StalePremiereTests/TodayScheduleTests territory (already covered there), not this file's job.
   * Skipping it here would make every "opened" test slot invalid — Opened/AutoOpened always
   * carries a movie for real — and crash the last-showing card's template on a null deref.
   */
  function slot(overrides: Partial<TodayScheduleSlotDto> = {}): TodayScheduleSlotDto {
    return {
      id: `s${++slotAutoId}`,
      scheduledFor: '2026-01-01T12:00:00Z',
      status: 'Scheduled',
      movie: { tmdbId: 1, title: 'Some Film', posterUrl: null, releaseYear: 2000, overview: null, voteAverage: 7, voteCount: 100 },
      totalClaps: null,
      myClaps: 0,
      myEmblemTier: null,
      ...overrides,
    };
  }

  function schedule(slots: TodayScheduleSlotDto[]): TodayScheduleDto {
    return { scopeId: 'global', slots };
  }

  // Per-test hooks for the calls the reconnect tests (#95) need to steer; every other test leaves them.
  let getImpl: () => Observable<PremiereDto>;
  let getActiveImpl: () => Observable<PremiereDto>;
  let lobbyImpl: () => Observable<LobbyDto>;
  let realtimeFake: {
    reconnected: Subject<void>;
    premiereOpened: Subject<unknown>;
    connected: WritableSignal<boolean>;
  };

  function make(
    loggedIn = true,
    anonSessionToken: string | null = null,
    todaySchedule: TodayScheduleDto | null = null,
  ) {
    getImpl = () => of(premiere());
    getActiveImpl = () => of(premiere());
    lobbyImpl = () => of(lobby());
    realtimeFake = { reconnected: new Subject<void>(), premiereOpened: new Subject(), connected: signal(true) };
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [PremiereComponent],
      providers: [
        provideRouter([]),
        {
          provide: PremiereService,
          useValue: {
            // A schedule to seed means these tests want the idle branch, reached the same way the
            // real app reaches it: getActive() 404s, which is what triggers loadTodaySchedule().
            getActive: () =>
              todaySchedule ? throwError(() => ({ status: 404 })) : getActiveImpl(),
            getToday: () => of(todaySchedule ?? schedule([])),
            lobby: () => lobbyImpl(),
            clap: () => of(premiere()),
            get: () => getImpl(),
          },
        },
        {
          provide: RealtimeService,
          useValue: {
            connected: realtimeFake.connected,
            connect: () => Promise.resolve(),
            watchPremiere: () => Promise.resolve(),
            stopWatching: () => Promise.resolve(),
            clapUpdates: new Subject(),
            premiereOpened: realtimeFake.premiereOpened,
            reconnected: realtimeFake.reconnected,
            premiereActivated: new Subject(),
          },
        },
        { provide: AuthService, useValue: { isLoggedIn: signal(loggedIn), user: signal(null) } },
        {
          provide: AnonymousSessionService,
          useValue: { session: signal(anonSessionToken ? { token: anonSessionToken } : null), ensure: () => Promise.resolve() },
        },
      ],
    });

    const fixture = TestBed.createComponent(PremiereComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  // --- Curtain / bulbs ---

  it('opens the curtain to at most 56% while still live, scaled from clap progress', () => {
    const c = make();
    c['premiere'].set(premiere({ totalClaps: 50, threshold: 100, status: 'Active' }));
    expect(c['curtainTravelPct']()).toBeCloseTo(28); // min(56, 50% * 0.56)

    c['premiere'].set(premiere({ totalClaps: 100, threshold: 100, status: 'Active' }));
    expect(c['curtainTravelPct']()).toBe(56); // capped even at 100% progress
  });

  it('snaps the curtain fully open once the Premiere has opened', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Opened', totalClaps: 10, threshold: 100 }));
    expect(c['curtainTravelPct']()).toBe(100);
  });

  it('keeps a floor of 6% lit bulbs while live even at zero claps, and lights all 22 once open', () => {
    const c = make();
    c['premiere'].set(premiere({ totalClaps: 0, threshold: 100, status: 'Active' }));
    const bulbs = c['bulbs']();
    expect(bulbs.length).toBe(22);
    expect(bulbs.some((b: { lit: boolean }) => b.lit)).toBe(true);

    c['premiere'].set(premiere({ status: 'AutoOpened' }));
    expect(c['bulbs']().every((b: { lit: boolean }) => b.lit)).toBe(true);
  });

  // --- Crowd note ---

  it('names up to three friends in the lobby, "and" before the last', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Active' }));
    c['lobby'].set(
      lobby({
        registeredCount: 3,
        faces: [
          { userId: '1', username: 'Ada', avatarUrl: null, isFriend: true },
          { userId: '2', username: 'Miles', avatarUrl: null, isFriend: true },
          { userId: '3', username: 'Rosa', avatarUrl: null, isFriend: true },
        ],
      }),
    );
    expect(c['crowdNote']()).toBe('Ada, Miles and Rosa are clapping');
  });

  it('falls back to a plain count, correctly singular, when nobody in the sample is a friend', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Active' }));
    c['lobby'].set(lobby({ registeredCount: 1 }));
    expect(c['crowdNote']()).toBe('1 person is clapping');

    c['lobby'].set(lobby({ registeredCount: 5 }));
    expect(c['crowdNote']()).toBe('5 people are clapping');
  });

  it('appends the anonymous count to the crowd note when there is one', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Active' }));
    c['lobby'].set(lobby({ registeredCount: 2, anonymousCount: 3 }));
    expect(c['crowdNote']()).toBe('2 people are clapping · 3 more in the crowd clapped anonymously');
  });

  it('uses singular "person opened" once revealed with exactly one contributor', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Opened', contributors: 1 }));
    c['lobby'].set(lobby());
    expect(c['crowdNote']()).toBe('1 person opened this Premiere together.');
  });

  it('names how many friends were in the crowd once revealed, still plural for more than one', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Opened', contributors: 5 }));
    c['lobby'].set(
      lobby({
        faces: [
          { userId: '1', username: 'Ada', avatarUrl: null, isFriend: true },
          { userId: '2', username: 'Miles', avatarUrl: null, isFriend: true },
        ],
      }),
    );
    expect(c['crowdNote']()).toBe('5 people opened this Premiere together, 2 of them your friends.');
  });

  // --- Clap button ---

  it('goes on/capped/off with cap status and open state', () => {
    const c = make();
    c['premiere'].set(premiere({ status: 'Active', myClaps: 2, registeredClapCap: 6 }));
    expect(c['clapButtonState']()).toBe('on');
    expect(c['clapButtonLabel']()).toBe('Clap');

    c['premiere'].set(premiere({ status: 'Active', myClaps: 6, registeredClapCap: 6 }));
    expect(c['clapButtonState']()).toBe('capped');
    expect(c['clapButtonLabel']()).toBe('You are capped');

    c['premiere'].set(premiere({ status: 'Opened', myClaps: 6, registeredClapCap: 6 }));
    expect(c['clapButtonState']()).toBe('off');
    expect(c['clapButtonLabel']()).toBe('In your library');
  });

  it('shows "No Premiere live" once there is nothing to clap for', () => {
    const c = make();
    c['premiere'].set(null);
    expect(c['clapButtonState']()).toBe('off');
    expect(c['clapButtonLabel']()).toBe('No Premiere live');
  });

  // --- Pips ---

  it('fills one pip per clap spent, up to the cap', () => {
    const c = make();
    c['premiere'].set(premiere({ myClaps: 3, myCap: 6 }));
    expect(c['pips']()).toEqual([true, true, true, false, false, false]);
    expect(c['pipsScaled']()).toBe(false);
  });

  it('keeps one pip per clap at exactly ten', () => {
    const c = make();
    c['premiere'].set(premiere({ myClaps: 4, myCap: 10 }));
    expect(c['pipsScaled']()).toBe(false);
    expect(c['pips']().filter(Boolean).length).toBe(4);
    expect(c['pips']().length).toBe(10);
  });

  // #96: one confirmed user makes the cap the whole threshold (30-50), which overflowed the card.
  it('draws ten pips above a cap of ten, each a tenth of the cap', () => {
    const c = make();
    const lit = (claps: number) => {
      c['premiere'].set(premiere({ myClaps: claps, myCap: 48 }));
      expect(c['pips']().length).toBe(10);
      return c['pips']().filter(Boolean).length;
    };
    expect(lit(0)).toBe(0);
    expect(lit(4)).toBe(0); // under a tenth of 48
    expect(lit(5)).toBe(1);
    expect(lit(10)).toBe(2);
    expect(lit(24)).toBe(5);
    expect(c['pipsScaled']()).toBe(true);
  });

  it('labels the pip row with the exact count for screen readers, scaled or not', () => {
    const c = make();
    c['premiere'].set(premiere({ myClaps: 3, myCap: 6 }));
    expect(c['pipsLabel']()).toBe('3 of 6 claps used');
    c['premiere'].set(premiere({ myClaps: 7, myCap: 48 }));
    expect(c['pipsLabel']()).toBe('7 of 48 claps used');
  });

  it('lights the last scaled pip only at the cap, so all lit always means capped', () => {
    const c = make();
    c['premiere'].set(premiere({ myClaps: 47, myCap: 48 }));
    expect(c['pips']().filter(Boolean).length).toBe(9);
    c['premiere'].set(premiere({ myClaps: 48, myCap: 48 }));
    expect(c['pips']().every(Boolean)).toBe(true);
  });

  // --- Faces ---

  it('draws initials only for a face with no avatar, and always assigns a colour', () => {
    const c = make();
    c['lobby'].set(
      lobby({
        faces: [
          { userId: '1', username: 'yourname', avatarUrl: null, isFriend: false },
          { userId: '2', username: 'photouser', avatarUrl: 'https://example.com/a.png', isFriend: false },
        ],
      }),
    );
    const [monogram, photo] = c['faces']();
    expect(monogram.initials).toBe('YO');
    expect(monogram.bg).toBeTruthy();
    expect(photo.initials).toBe('');
    expect(photo.avatarUrl).toBe('https://example.com/a.png');
  });

  // --- Anonymous participation (issue #57) ---

  it('lets a signed-out visitor with a valid anonymous session participate', () => {
    const c = make(false, 'anon-token');
    expect(c['canParticipate']()).toBe(true);
  });

  it('does not let a signed-out visitor with no session participate', () => {
    const c = make(false, null);
    expect(c['canParticipate']()).toBe(false);
  });

  it('reads the cap from myCap, not the registered cap — the anonymous cap is lower', () => {
    const c = make(false, 'anon-token');
    c['premiere'].set(
      premiere({ status: 'Active', myClaps: 1, myCap: 2, registeredClapCap: 6, anonymousClapCap: 2 }),
    );
    expect(c['capReached']()).toBe(false);
    expect(c['pips']()).toEqual([true, false]);
  });

  it('contrasts the visitor cap against the registered one, both capped and not', () => {
    const c = make(false, 'anon-token');
    c['premiere'].set(
      premiere({ status: 'Active', myClaps: 1, myCap: 2, registeredClapCap: 6 }),
    );
    expect(c['capNote']()).toBe('Visitors get 2 claps and keep nothing. An account gets you 6 and the film.');

    c['premiere'].set(premiere({ status: 'Active', myClaps: 2, myCap: 2, registeredClapCap: 6 }));
    expect(c['capNote']()).toBe('That is the whole visitor cap of 2 claps — an account gets you 6');
  });

  it('promises no one opens a Premiere alone only while the cap is below the threshold', () => {
    const c = make();
    c['premiere'].set(premiere({ myCap: 6, threshold: 100 }));
    expect(c['capNote']()).toBe('Cap of 6 claps per person, so no one opens a Premiere alone');

    // #96: one confirmed user makes the cap the whole threshold (§4.2's small-count limitation).
    c['premiere'].set(premiere({ myCap: 48, threshold: 48 }));
    expect(c['capNote']()).toBe('You could open this one yourself — or bring friends.');
  });

  it('says to create an account, not "in your library", once revealed for a signed-out viewer', () => {
    const c = make(false, 'anon-token');
    c['premiere'].set(premiere({ status: 'Opened', myClaps: 2, myCap: 2 }));
    expect(c['clapButtonLabel']()).toBe('Create an account');
    expect(c['revealVisitor']()).toBe(true);
  });

  it('never marks a visitor reveal for a signed-in viewer', () => {
    const c = make(true);
    c['premiere'].set(premiere({ status: 'Opened' }));
    expect(c['revealVisitor']()).toBe(false);
  });

  it('draws faceless, ringless discs for a visitor instead of the real lobby faces', () => {
    const c = make(false, 'anon-token');
    c['premiere'].set(premiere({ status: 'Active' }));
    // The backend never hands a stranger real identities — Faces stays empty even though people
    // clapped, and the client is meant to draw min(9, registeredCount) blanks instead.
    c['lobby'].set(
      lobby({
        registeredCount: 12,
        faces: [{ userId: '1', username: 'Ada', avatarUrl: null, isFriend: true }],
      }),
    );
    const faces = c['faces']();
    expect(faces.length).toBe(9);
    expect(faces.every((f: { initials: string; isFriend: boolean }) => f.initials === '' && !f.isFriend)).toBe(true);
  });

  it('never names a friend or a registered count to a visitor in the crowd note', () => {
    const c = make(false, 'anon-token');
    c['premiere'].set(premiere({ status: 'Active' }));

    c['lobby'].set(lobby({ registeredCount: 4, anonymousCount: 3 }));
    expect(c['crowdNote']()).toBe('3 more clapped anonymously, like you. Sign in to see which of your friends are here.');

    c['lobby'].set(lobby({ registeredCount: 4, anonymousCount: 0 }));
    expect(c['crowdNote']()).toBe('Sign in to see which of your friends are here.');
  });

  it('tells a visitor how many contributors keep the film once revealed', () => {
    const c = make(false, 'anon-token');
    c['premiere'].set(premiere({ status: 'Opened', contributors: 5 }));
    c['lobby'].set(lobby({ registeredCount: 3 }));
    expect(c['crowdNote']()).toBe('5 people opened this Premiere together. 3 of them keep the film.');
  });

  // --- Idle state: the sign as a clock, today's programme, last night's showing (issue #58) ---

  it('loads today\'s schedule when getActive() 404s, the same trigger the real 404 path uses', () => {
    const s = schedule([slot({ id: 'a' }), slot({ id: 'b' })]);
    const c = make(true, null, s);
    expect(c['todaySchedule']()).toEqual(s);
  });

  it('targets the countdown at the earliest Scheduled slot, ignoring later ones', () => {
    // Slots arrive earliest-first — GET /premieres/today's own documented ordering — so finding
    // "next" is just the first Scheduled one; nothing here re-sorts by time defensively.
    const c = make(
      true,
      null,
      schedule([
        slot({ id: 'next', status: 'Scheduled', scheduledFor: '2026-01-01T09:00:00Z' }),
        slot({ id: 'later', status: 'Scheduled', scheduledFor: '2026-01-01T20:00:00Z' }),
      ]),
    );
    expect(c['nextSlot']().id).toBe('next');
  });

  it('is day-wrapped only once nothing is left to count down to', () => {
    const withNext = make(
      true,
      null,
      schedule([slot({ status: 'AutoOpened', totalClaps: 1 }), slot({ status: 'Scheduled' })]),
    );
    expect(withNext['dayWrapped']()).toBe(false);

    const allDone = make(true, null, schedule([slot({ status: 'AutoOpened', totalClaps: 1 })]));
    expect(allDone['dayWrapped']()).toBe(true);
    expect(allDone['idleKicker']()).toBe('Closed for tonight');
  });

  it('flags "nothing opened yet" and picks the right kicker for a fresh day', () => {
    const c = make(
      true,
      null,
      schedule([slot({ status: 'Scheduled' }), slot({ status: 'Scheduled' })]),
    );
    expect(c['noneOpenedYet']()).toBe(true);
    expect(c['idleKicker']()).toBe('Not open yet');
    expect(c['idleCaption']()).toBe('two Premieres scheduled today');
  });

  it('captions the upcoming slot by its ordinal position, "and last" only for the final one', () => {
    const midDay = make(
      true,
      null,
      schedule([
        slot({ id: '1', status: 'AutoOpened', totalClaps: 1 }),
        slot({ id: '2', status: 'Scheduled' }),
        slot({ id: '3', status: 'Scheduled' }),
      ]),
    );
    expect(midDay['idleCaption']()).toBe('The second Premiere today');

    const lastOfDay = make(
      true,
      null,
      schedule([
        slot({ id: '1', status: 'AutoOpened', totalClaps: 1 }),
        slot({ id: '2', status: 'AutoOpened', totalClaps: 1 }),
        slot({ id: '3', status: 'Scheduled' }),
      ]),
    );
    expect(lastOfDay['idleCaption']()).toBe('The third and last Premiere today');
  });

  it('picks the chronologically last opened slot as last night\'s showing', () => {
    const c = make(
      true,
      null,
      schedule([
        slot({ id: 'earlier', status: 'AutoOpened', totalClaps: 1, scheduledFor: '2026-01-01T09:00:00Z' }),
        slot({ id: 'latest', status: 'Opened', totalClaps: 1, scheduledFor: '2026-01-01T13:00:00Z' }),
        slot({ id: 'future', status: 'Scheduled' }),
      ]),
    );
    expect(c['lastShowingSlot']().id).toBe('latest');
  });

  it('tells "attended" apart from "missed" by the slot\'s own myClaps', () => {
    const attended = make(true, null, schedule([slot({ status: 'AutoOpened', myClaps: 6 })]));
    expect(attended['attendedLastShowing']()).toBe(true);

    const missed = make(true, null, schedule([slot({ status: 'AutoOpened', myClaps: 0 })]));
    expect(missed['attendedLastShowing']()).toBe(false);
  });

  it('builds ledger rows that mark the next slot, and Scheduled/Missed slots elsewhere as dead', () => {
    const c = make(
      true,
      null,
      schedule([
        slot({ id: 'ran', status: 'AutoOpened', totalClaps: 1 }),
        slot({ id: 'missed', status: 'Missed' }),
        slot({ id: 'next', status: 'Scheduled' }),
        slot({ id: 'later', status: 'Scheduled' }),
      ]),
    );
    const rows = c['ledgerRows']() as { id: string; isNext: boolean; isDead: boolean; isMissed: boolean }[];

    expect(rows.find((r) => r.id === 'ran')).toEqual(jasmine.objectContaining({ isNext: false, isDead: false }));
    expect(rows.find((r) => r.id === 'missed')).toEqual(jasmine.objectContaining({ isDead: true, isMissed: true }));
    expect(rows.find((r) => r.id === 'next')).toEqual(jasmine.objectContaining({ isNext: true, isDead: false }));
    expect(rows.find((r) => r.id === 'later')).toEqual(
      jasmine.objectContaining({ isNext: false, isDead: true, isMissed: false }),
    );
  });

  it('reports how many of today\'s Premieres have run, "None" spelled out rather than "Zero"', () => {
    const none = make(true, null, schedule([slot({ status: 'Scheduled' }), slot({ status: 'Scheduled' })]));
    expect(none['programmeSubtext']()).toBe('None have run');

    const some = make(
      true,
      null,
      schedule([slot({ status: 'AutoOpened', totalClaps: 1 }), slot({ status: 'Scheduled' })]),
    );
    expect(some['programmeSubtext']()).toBe('One of two have run');
  });

  describe('catching up after a missed reveal (#95)', () => {
    const revealed = () =>
      premiere({
        status: 'Opened',
        totalClaps: 120,
        openedAt: '2026-01-01T00:30:00Z',
        movie: { tmdbId: 1, title: 'The Volcano', posterUrl: null, releaseYear: 1997, overview: null, voteAverage: 6, voteCount: 900 },
      });

    const next = () => premiere({ id: 'p2', totalClaps: 0 });

    /** The server's state after the gap: the Premiere on screen has opened and nothing is live. */
    function makeAfterGap() {
      const c = make();
      getActiveImpl = () => throwError(() => ({ status: 404 }));
      return c;
    }

    it('shows the reveal and stops polling the lobby once reconnected after a missed broadcast', () => {
      const c = makeAfterGap();
      const stopLobby = spyOn(c as any, 'stopLobbyPolling').and.callThrough();
      expect(c['premiere']().status).toBe('Active');

      // The reveal went out while the socket was down; only the server knows the Premiere opened.
      getImpl = () => of(revealed());
      realtimeFake.reconnected.next();

      expect(c['premiere']().status).toBe('Opened');
      expect(c['premiere']().movie?.title).toBe('The Volcano');
      expect(stopLobby).toHaveBeenCalled();
    });

    it('also catches up when the lobby poll starts 404ing, without waiting for a reconnect', () => {
      const c = makeAfterGap();
      getImpl = () => of(revealed());
      lobbyImpl = () => throwError(() => ({ status: 404 }));

      c['fetchLobby']();

      expect(c['premiere']().status).toBe('Opened');
    });

    it('leaves a still-live Premiere on screen after a reconnect, just refreshed', () => {
      const c = makeAfterGap();
      getImpl = () => of(premiere({ totalClaps: 40 }));
      realtimeFake.reconnected.next();

      expect(c['premiere']().status).toBe('Active');
      expect(c['premiere']().totalClaps).toBe(40);
    });

    it('does not refetch on a lobby network error, only on a 404', () => {
      const c = makeAfterGap();
      const get = jasmine.createSpy('get').and.returnValue(of(premiere()));
      getImpl = get;

      lobbyImpl = () => throwError(() => ({ status: 0 }));
      c['fetchLobby']();
      expect(get).not.toHaveBeenCalled();

      lobbyImpl = () => throwError(() => ({ status: 404 }));
      c['fetchLobby']();
      expect(get).toHaveBeenCalledTimes(1);
    });

    it('clears a stale "could not load" error once it has caught up', () => {
      const c = makeAfterGap();
      c['error'].set('Could not load the Premiere.');
      getImpl = () => of(revealed());

      realtimeFake.reconnected.next();

      expect(c['error']()).toBeNull();
    });

    it('never lets a slower read pull a live Premiere\'s counts backwards', () => {
      const c = makeAfterGap();
      c['premiere'].set(premiere({ totalClaps: 50, myClaps: 3, contributors: 9 }));
      getImpl = () => of(premiere({ totalClaps: 40, myClaps: 2, contributors: 7 }));

      realtimeFake.reconnected.next();

      expect(c['premiere']().totalClaps).toBe(50);
      expect(c['premiere']().myClaps).toBe(3);
      expect(c['premiere']().contributors).toBe(9);
    });

    it('catches up when /active 404s on a non-initial load and a live Premiere is still on screen', () => {
      const c = makeAfterGap();
      getImpl = () => of(revealed());

      c['load'](false);

      expect(c['premiere']().status).toBe('Opened');
    });

    it('drops a slow refetch if the page has since moved on to a different Premiere', () => {
      const c = makeAfterGap();
      const pending = new Subject<PremiereDto>();
      getImpl = () => pending;
      realtimeFake.reconnected.next();

      c['premiere'].set(premiere({ id: 'p2' }));
      const onRevealed = spyOn(c as any, 'onRevealed');
      pending.next(revealed()); // the reveal of p1, arriving late

      expect(c['premiere']().id).toBe('p2');
      expect(c['premiere']().status).toBe('Active');
      expect(onRevealed).not.toHaveBeenCalled();
    });

    it('does not reveal twice when the event beats the refetch', () => {
      const c = makeAfterGap();
      const pending = new Subject<PremiereDto>();
      getImpl = () => pending;
      const onRevealed = spyOn(c as any, 'onRevealed').and.callThrough();
      realtimeFake.reconnected.next();

      realtimeFake.premiereOpened.next({
        premiereId: 'p1',
        status: 'Opened',
        totalClaps: 120,
        contributors: 0,
        openedAt: '2026-01-01T00:30:00Z',
        movie: revealed().movie,
      });
      pending.next(revealed());

      expect(c['premiere']().status).toBe('Opened');
      expect(onRevealed).toHaveBeenCalledTimes(1);
    });

    it('does not refetch a Premiere that is already revealed', () => {
      const c = makeAfterGap();
      c['premiere'].set(revealed());
      const get = jasmine.createSpy('get').and.returnValue(of(premiere()));
      getImpl = get;

      realtimeFake.reconnected.next();

      expect(get).not.toHaveBeenCalled();
    });

    it('picks up the next Premiere if its activation was missed in the same gap as the reveal', () => {
      const c = makeAfterGap();
      getImpl = () => of(revealed());
      getActiveImpl = () => of(next());

      realtimeFake.reconnected.next();

      expect(c['premiere']().id).toBe('p2');
      expect(c['premiere']().status).toBe('Active');
    });

    it('picks up a new Premiere on reconnect when the one on screen was already revealed', () => {
      const c = makeAfterGap();
      c['premiere'].set(revealed());
      getActiveImpl = () => of(next());

      realtimeFake.reconnected.next();

      expect(c['premiere']().id).toBe('p2');
    });

    it('keeps the reveal on screen when nothing new went live during the gap', () => {
      const c = makeAfterGap();
      c['premiere'].set(revealed());
      const active = jasmine.createSpy('getActive').and.returnValue(throwError(() => ({ status: 404 })));
      getActiveImpl = active;

      realtimeFake.reconnected.next();

      expect(active).toHaveBeenCalledTimes(1);
      expect(c['premiere']().status).toBe('Opened');
    });

    it('probes /active exactly once after a catch-up reveal, even when nothing is live', () => {
      // The 404 routes back into refreshCurrent(), which must stop because the reveal is on screen.
      const c = makeAfterGap();
      const active = jasmine.createSpy('getActive').and.returnValue(throwError(() => ({ status: 404 })));
      getActiveImpl = active;
      const get = jasmine.createSpy('get').and.returnValue(of(revealed()));
      getImpl = get;

      realtimeFake.reconnected.next();

      expect(get).toHaveBeenCalledTimes(1);
      expect(active).toHaveBeenCalledTimes(1);
      expect(c['premiere']().status).toBe('Opened');
    });

    it('clears a stale "could not load" banner when the probe finds a new Premiere', () => {
      const c = makeAfterGap();
      c['premiere'].set(revealed());
      c['error'].set('Could not load the Premiere.');
      getActiveImpl = () => of(next());

      realtimeFake.reconnected.next();

      expect(c['premiere']().id).toBe('p2');
      expect(c['error']()).toBeNull();
    });

    it('does not probe /active on the normal reveal broadcast', () => {
      const c = makeAfterGap();
      const active = jasmine.createSpy('getActive').and.returnValue(of(next()));
      getActiveImpl = active;

      realtimeFake.premiereOpened.next({
        premiereId: 'p1',
        status: 'Opened',
        totalClaps: 120,
        contributors: 0,
        openedAt: '2026-01-01T00:30:00Z',
        movie: revealed().movie,
      });

      expect(active).not.toHaveBeenCalled();
      expect(c['premiere']().id).toBe('p1');
    });
  });
});
