import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { UserFriendsComponent } from './user-friends.component';
import { UsersService } from '../../core/users.service';
import { FriendsService } from '../../core/friends.service';
import { AuthService } from '../../core/auth.service';
import { FriendDto, LimitedProfileDto } from '../../core/models';

/**
 * A read-only view of someone else's friend list, with one exception: your own list gets a Remove
 * action (issue #66 — moved here from the friends page's inline copy, which #60 pulled out). The
 * behaviour worth pinning down: the search debounce (same shape as FriendsComponent's own), telling
 * a 403 (private account) apart from a genuine error, and Remove's two-step confirm.
 */
describe('UserFriendsComponent', () => {
  let friendsOfSpy: jasmine.Spy;
  let removeSpy: jasmine.Spy;
  let sendRequestSpy: jasmine.Spy;
  let profileSpy: jasmine.Spy;

  function friend(username: string, id = username): FriendDto {
    return { userId: id, username, bio: null, isPrivate: false, friendsSince: '2026-01-01T00:00:00Z' };
  }

  function limitedProfile(overrides: Partial<LimitedProfileDto> = {}): LimitedProfileDto {
    return {
      username: 'ana',
      avatarUrl: null,
      friendshipStatus: null,
      friendRequestOutgoing: null,
      sharedPremieresAttended: null,
      ...overrides,
    };
  }

  /**
   * Defaults to a viewer who is not "ana" — most tests here are about a stranger or friend looking
   * at someone else's list, not the "this is your own" header case, which gets its own tests below.
   */
  function make(
    result: FriendDto[] | (() => ReturnType<typeof throwError>) = [],
    viewerUsername: string | null = 'someone-else',
    profileResult: LimitedProfileDto | (() => ReturnType<typeof throwError>) = limitedProfile(),
    removeResult: (() => ReturnType<typeof throwError>) | null = null,
  ) {
    TestBed.resetTestingModule();
    friendsOfSpy = jasmine.createSpy('friendsOf').and.returnValue(
      typeof result === 'function' ? result() : of(result),
    );
    removeSpy = jasmine.createSpy('remove').and.returnValue(removeResult ? removeResult() : of(undefined));
    sendRequestSpy = jasmine.createSpy('sendRequest').and.returnValue(of({}));
    profileSpy = jasmine
      .createSpy('profile')
      .and.returnValue(typeof profileResult === 'function' ? profileResult() : of(profileResult));

    TestBed.configureTestingModule({
      imports: [UserFriendsComponent],
      providers: [
        provideRouter([]),
        { provide: UsersService, useValue: { friendsOf: friendsOfSpy, profile: profileSpy } },
        { provide: FriendsService, useValue: { remove: removeSpy, sendRequest: sendRequestSpy } },
        { provide: AuthService, useValue: { user: signal(viewerUsername ? { username: viewerUsername } : null) } },
      ],
    });

    const fixture = TestBed.createComponent(UserFriendsComponent);
    fixture.componentRef.setInput('username', 'ana');
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('loads the list for the routed username on init', () => {
    make([friend('bob')]);

    expect(friendsOfSpy).toHaveBeenCalledWith('ana', '');
  });

  it('sends one search request per pause, not one per keystroke', fakeAsync(() => {
    const c = make([]);
    friendsOfSpy.calls.reset();

    c['onSearchInput']('b');
    c['onSearchInput']('bo');
    c['onSearchInput']('bob');
    tick(300);

    expect(friendsOfSpy).toHaveBeenCalledTimes(1);
    expect(friendsOfSpy).toHaveBeenCalledWith('ana', 'bob');
  }));

  it('clears the search and reloads the unfiltered list', fakeAsync(() => {
    const c = make([]);
    c['onSearchInput']('bob');
    tick(300);
    friendsOfSpy.calls.reset();

    c['clearSearch']();

    expect(c['query']()).toBe('');
    expect(friendsOfSpy).toHaveBeenCalledWith('ana', '');
  }));

  it('treats a 403 as "this account is private", not as an error banner', () => {
    const c = make(() => throwError(() => new HttpErrorResponse({ status: 403 })));

    expect(c['forbidden']()).toBe(true);
    expect(c['error']()).toBeNull();
  });

  it('treats any other failure as a real error, not as forbidden', () => {
    const c = make(() => throwError(() => new HttpErrorResponse({ status: 500 })));

    expect(c['forbidden']()).toBe(false);
    expect(c['error']()).not.toBeNull();
  });

  it('lists the friends returned for an entitled viewer', () => {
    const c = make([friend('bob'), friend('carol')]);

    expect(c['friends']().map((f: FriendDto) => f.username)).toEqual(['bob', 'carol']);
    expect(c['forbidden']()).toBe(false);
  });

  it('recognises the viewer looking at their own friend list', () => {
    const c = make([], 'ana');

    expect(c['isSelf']()).toBe(true);
  });

  it('does not treat a stranger or an anonymous viewer as self', () => {
    expect(make([], 'someone-else')['isSelf']()).toBe(false);
    expect(make([], null)['isSelf']()).toBe(false);
  });

  it('sets the total count from an unfiltered load but not a search', fakeAsync(() => {
    const c = make([friend('bob'), friend('carol')]);

    expect(c['totalCount']()).toBe(2);

    friendsOfSpy.and.returnValue(of([friend('bob')]));
    c['onSearchInput']('bo');
    tick(300);

    // The search narrowed what's shown, not the true size of the list.
    expect(c['friends']().length).toBe(1);
    expect(c['totalCount']()).toBe(2);
  }));

  describe('removing a friend (own list only)', () => {
    it('asks before removing, and Keep backs out without calling the API', () => {
      const c = make([friend('bob')], 'ana');

      c['ask']('bob');
      expect(c['askingId']()).toBe('bob');

      c['cancelAsk']();
      expect(c['askingId']()).toBeNull();
      expect(removeSpy).not.toHaveBeenCalled();
    });

    it('removes the row and decrements the count on confirm', () => {
      const c = make([friend('bob'), friend('carol')], 'ana');
      expect(c['totalCount']()).toBe(2);

      c['confirmRemove'](friend('bob'));

      expect(removeSpy).toHaveBeenCalledWith('bob');
      expect(c['friends']().map((f: FriendDto) => f.username)).toEqual(['carol']);
      expect(c['totalCount']()).toBe(1);
      expect(c['removedNotice']()).toBe('You and bob are no longer friends.');
    });

    it('leaves the row in place and reports an error if the removal fails', () => {
      const c = make(
        [friend('bob')],
        'ana',
        limitedProfile(),
        () => throwError(() => new HttpErrorResponse({ status: 500 })),
      );

      c['confirmRemove'](friend('bob'));

      expect(c['friends']().length).toBe(1);
      expect(c['error']()).toBe('Could not remove bob.');
    });
  });

  describe('the private-account refusal (issue #66)', () => {
    it('offers Add friend for a stranger with nothing pending either way', () => {
      const c = make(
        () => throwError(() => new HttpErrorResponse({ status: 403 })),
        'someone-else',
        limitedProfile({ friendshipStatus: null }),
      );

      expect(c['canAdd']()).toBe(true);
      expect(c['relationship']()).toBeNull();
    });

    it('shows "Request sent" with no action for an outgoing pending request', () => {
      const c = make(
        () => throwError(() => new HttpErrorResponse({ status: 403 })),
        'someone-else',
        limitedProfile({ friendshipStatus: 'Pending', friendRequestOutgoing: true }),
      );

      expect(c['relationship']()).toBe('Request sent');
      expect(c['canAdd']()).toBe(false);
      expect(c['linkToRequests']()).toBe(false);
    });

    it('points at the Friends screen for an incoming pending request, rather than guessing a request id', () => {
      const c = make(
        () => throwError(() => new HttpErrorResponse({ status: 403 })),
        'someone-else',
        limitedProfile({ friendshipStatus: 'Pending', friendRequestOutgoing: false }),
      );

      expect(c['relationship']()).toBe('Wants to be friends');
      expect(c['linkToRequests']()).toBe(true);
    });

    it('sends a request and refreshes the relationship on Add friend', () => {
      const c = make(
        () => throwError(() => new HttpErrorResponse({ status: 403 })),
        'someone-else',
        limitedProfile({ friendshipStatus: null }),
      );
      profileSpy.and.returnValue(of(limitedProfile({ friendshipStatus: 'Pending', friendRequestOutgoing: true })));

      c['addFriend']();

      expect(sendRequestSpy).toHaveBeenCalledWith('ana');
      expect(c['relationship']()).toBe('Request sent');
      expect(c['addBusy']()).toBe(false);
    });
  });
});
