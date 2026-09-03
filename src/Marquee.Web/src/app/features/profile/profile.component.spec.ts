import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProfileComponent } from './profile.component';
import { UsersService } from '../../core/users.service';
import { FriendsService } from '../../core/friends.service';
import { AuthService } from '../../core/auth.service';
import { FullProfileDto, LimitedProfileDto, ProfileDto, isFullProfile } from '../../core/models';

/**
 * The two payload shapes.
 *
 * GET /api/users/{username} returns either a full profile or, for a stranger viewing a private
 * account, one carrying only username and bio — the remaining fields absent rather than null. The
 * screen has to render whichever it was handed without deciding for itself what the viewer is
 * entitled to: the server already made that call, and a friend sees everything even when the
 * account is private. These cover that split.
 */
describe('ProfileComponent payload shapes', () => {
  const me = 'me-id';

  function full(overrides: Partial<FullProfileDto> = {}): FullProfileDto {
    return {
      id: 'other-id',
      username: 'ana',
      bio: 'Likes westerns.',
      avatarUrl: null,
      isPrivate: false,
      createdAt: '2026-01-01T00:00:00Z',
      moviesCollected: 3,
      premieresAttended: 5,
      friendCount: 2,
      friendshipStatus: null,
      friendRequestOutgoing: null,
      sharedPremieresAttended: null,
      ...overrides,
    };
  }

  function limited(overrides: Partial<LimitedProfileDto> = {}): LimitedProfileDto {
    return {
      username: 'ana',
      bio: 'Likes westerns.',
      avatarUrl: null,
      friendshipStatus: null,
      friendRequestOutgoing: null,
      sharedPremieresAttended: null,
      ...overrides,
    };
  }

  let sendRequestSpy: jasmine.Spy;

  function make(profile: ProfileDto, viewerId: string | null = me) {
    // Reset first so a test can build more than one profile — the outgoing/incoming pair below
    // needs two, and TestBed refuses to be reconfigured once instantiated.
    TestBed.resetTestingModule();
    sendRequestSpy = jasmine.createSpy('sendRequest').and.returnValue(of({}));

    TestBed.configureTestingModule({
      imports: [ProfileComponent],
      providers: [
        provideRouter([]),
        { provide: UsersService, useValue: { profile: () => of(profile), updateMe: () => of({}) } },
        { provide: FriendsService, useValue: { sendRequest: sendRequestSpy } },
        {
          provide: AuthService,
          useValue: {
            user: signal(viewerId ? { id: viewerId, username: 'me' } : null),
            applyProfileUpdate: () => {},
          },
        },
      ],
    });

    const fixture = TestBed.createComponent(ProfileComponent);
    fixture.componentRef.setInput('username', 'ana');
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('narrows a full payload so the activity figures are available', () => {
    const c = make(full());

    expect(c['full']()).not.toBeNull();
    expect(c['limited']()).toBe(false);
    expect(c['full']().moviesCollected).toBe(3);
  });

  it('treats a payload without an id as the limited shape', () => {
    // Keyed on a field only the full payload carries — never on isPrivate, which the limited
    // payload does not even include.
    const c = make(limited());

    expect(c['full']()).toBeNull();
    expect(c['limited']()).toBe(true);
  });

  it('offers Add Friend on a limited profile with no relationship yet', () => {
    // The reason friendshipStatus and friendRequestOutgoing were added to LimitedProfileDto at
    // all: without them, a stranger's private-profile view had nothing to build this button from.
    const c = make(limited({ friendshipStatus: null }));

    expect(c['canAdd']()).toBe(true);

    c['addFriend']();

    expect(sendRequestSpy).toHaveBeenCalledWith('ana');
  });

  it('does not offer Add Friend on a limited profile with a request already pending', () => {
    const c = make(limited({ friendshipStatus: 'Pending', friendRequestOutgoing: true }));

    expect(c['canAdd']()).toBe(false);
    expect(c['relationship']()).toBe('Request sent');
  });

  it('points an incoming request on a limited profile at Friends, same as a full one', () => {
    const c = make(limited({ friendshipStatus: 'Pending', friendRequestOutgoing: false }));

    expect(c['relationship']()).toBe('Wants to be friends');
    expect(c['linkToRequests']()).toBe(true);
  });

  it('recognises the viewer looking at their own profile', () => {
    const c = make(full({ id: me }));

    expect(c['isSelf']()).toBe(true);
    // Nothing to add, and no relationship to describe, when it is you.
    expect(c['canAdd']()).toBe(false);
    expect(c['relationship']()).toBeNull();
  });

  it('offers to add a stranger with no relationship yet', () => {
    const c = make(full({ friendshipStatus: null }));

    expect(c['canAdd']()).toBe(true);
    expect(c['relationship']()).toBeNull();
  });

  it('describes an accepted friendship rather than offering to add again', () => {
    const c = make(full({ friendshipStatus: 'Accepted' }));

    expect(c['relationship']()).toBe('Friends');
    expect(c['canAdd']()).toBe(false);
  });

  it('distinguishes a request sent from one received', () => {
    // The two nullable fields only mean something together: Pending alone does not say which way
    // the request points.
    const sent = make(full({ friendshipStatus: 'Pending', friendRequestOutgoing: true }));
    expect(sent['relationship']()).toBe('Request sent');
    expect(sent['linkToRequests']()).toBe(false);

    const received = make(full({ friendshipStatus: 'Pending', friendRequestOutgoing: false }));
    expect(received['relationship']()).toBe('Wants to be friends');
    // Accepting needs the request's id, which this payload does not carry — so the screen points
    // at the one that owns requests rather than guessing.
    expect(received['linkToRequests']()).toBe(true);
  });
});

describe('isFullProfile', () => {
  it('does not mistake a private full profile for a limited one', () => {
    // The trap this guard exists to avoid: a friend viewing a private account gets the *full*
    // payload with isPrivate true. Branching on isPrivate would wrongly hide everything.
    const privateButEntitled: FullProfileDto = {
      id: 'x',
      username: 'ana',
      bio: null,
      avatarUrl: null,
      isPrivate: true,
      createdAt: '2026-01-01T00:00:00Z',
      moviesCollected: 1,
      premieresAttended: 1,
      friendCount: 1,
      friendshipStatus: 'Accepted',
      friendRequestOutgoing: null,
      sharedPremieresAttended: null,
    };

    const limitedProfile: LimitedProfileDto = {
      username: 'ana',
      bio: null,
      avatarUrl: null,
      friendshipStatus: null,
      friendRequestOutgoing: null,
      sharedPremieresAttended: null,
    };

    expect(isFullProfile(privateButEntitled)).toBe(true);
    expect(isFullProfile(limitedProfile)).toBe(false);
  });
});
