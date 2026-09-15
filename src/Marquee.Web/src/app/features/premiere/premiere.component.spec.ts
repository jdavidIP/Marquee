import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, throwError, Subject } from 'rxjs';
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

  function make(
    loggedIn = true,
    anonSessionToken: string | null = null,
    todaySchedule: TodayScheduleDto | null = null,
  ) {
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
              todaySchedule ? throwError(() => ({ status: 404 })) : of(premiere()),
            getToday: () => of(todaySchedule ?? schedule([])),
            lobby: () => of(lobby()),
            clap: () => of(premiere()),
            get: () => of(premiere()),
          },
        },
        {
          provide: RealtimeService,
          useValue: {
            connected: signal(true),
            connect: () => Promise.resolve(),
            watchPremiere: () => Promise.resolve(),
            stopWatching: () => Promise.resolve(),
            clapUpdates: new Subject(),
            premiereOpened: new Subject(),
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
});
