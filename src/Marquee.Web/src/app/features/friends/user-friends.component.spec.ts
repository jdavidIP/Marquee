import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { UserFriendsComponent } from './user-friends.component';
import { UsersService } from '../../core/users.service';
import { AuthService } from '../../core/auth.service';
import { FriendDto } from '../../core/models';

/**
 * A read-only view of someone else's friend list. The behaviour worth pinning down: the search
 * debounce (same shape as FriendsComponent's own), and telling a 403 (private account) apart from
 * a genuine error — the API returns 403 specifically so this screen can say "private" rather than
 * a generic failure banner.
 */
describe('UserFriendsComponent', () => {
  let friendsOfSpy: jasmine.Spy;

  function friend(username: string): FriendDto {
    return { userId: username, username, bio: null, isPrivate: false, friendsSince: '2026-01-01T00:00:00Z' };
  }

  /**
   * Defaults to a viewer who is not "ana" — most tests here are about a stranger or friend looking
   * at someone else's list, not the "this is your own" header case, which gets its own tests below.
   */
  function make(
    result: FriendDto[] | (() => ReturnType<typeof throwError>) = [],
    viewerUsername: string | null = 'someone-else',
  ) {
    TestBed.resetTestingModule();
    friendsOfSpy = jasmine.createSpy('friendsOf').and.returnValue(
      typeof result === 'function' ? result() : of(result),
    );

    TestBed.configureTestingModule({
      imports: [UserFriendsComponent],
      providers: [
        provideRouter([]),
        { provide: UsersService, useValue: { friendsOf: friendsOfSpy } },
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
});
