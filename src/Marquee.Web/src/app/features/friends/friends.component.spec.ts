import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { FriendsComponent } from './friends.component';
import { FriendsService } from '../../core/friends.service';
import { UsersService } from '../../core/users.service';
import { AuthService } from '../../core/auth.service';
import { FriendDto, FriendRequestDto, UserSearchResultDto } from '../../core/models';

/**
 * The relationship a search hit is rendered with.
 *
 * This is the screen's one piece of real branching logic, and it is what stops the results list
 * offering "Add friend" for someone who is already a friend, already asked you, or is you — each of
 * which the server would refuse with a 4xx. Showing the outcome up front rather than letting someone
 * discover it by rejection is the same reasoning the admin editors follow.
 */
describe('FriendsComponent search relations', () => {
  const me = 'me-id';

  function make(options: {
    friends?: FriendDto[];
    requests?: FriendRequestDto[];
    results?: UserSearchResultDto[];
  }) {
    const friendsApi = {
      friends: () => of(options.friends ?? []),
      requests: () => of(options.requests ?? []),
    };

    TestBed.configureTestingModule({
      imports: [FriendsComponent],
      providers: [
        { provide: FriendsService, useValue: friendsApi },
        { provide: UsersService, useValue: { search: () => of(options.results ?? []) } },
        { provide: AuthService, useValue: { user: signal({ id: me, username: 'me' }) } },
      ],
    });

    const fixture = TestBed.createComponent(FriendsComponent);
    fixture.detectChanges(); // runs ngOnInit, loading friends and requests

    const component = fixture.componentInstance as unknown as Record<string, any>;
    component['results'].set(options.results ?? []);
    return component;
  }

  function hit(id: string, username: string): UserSearchResultDto {
    return { id, username, bio: null, isPrivate: false };
  }

  it('marks the signed-in user as themselves rather than offering to add them', () => {
    const c = make({ results: [hit(me, 'me')] });

    expect(c['searchRows']()[0].relation).toBe('self');
  });

  it('marks an existing friend rather than offering to add them again', () => {
    const c = make({
      friends: [{ userId: 'u1', username: 'ana', bio: null, isPrivate: false, friendsSince: '' }],
      results: [hit('u1', 'ana')],
    });

    expect(c['searchRows']()[0].relation).toBe('friend');
  });

  it('marks someone already asked, so the button does not invite a duplicate request', () => {
    const c = make({
      requests: [
        { id: 'r1', userId: 'u2', username: 'bo', status: 'Pending', outgoing: true, createdAt: '' },
      ],
      results: [hit('u2', 'bo')],
    });

    expect(c['searchRows']()[0].relation).toBe('requested');
  });

  it('offers to accept someone who already asked you, carrying their request id', () => {
    // The reciprocal case. Sending a request here would work — the server treats it as accepting
    // theirs — but showing "Accept" says what is actually happening.
    const c = make({
      requests: [
        { id: 'r9', userId: 'u3', username: 'cy', status: 'Pending', outgoing: false, createdAt: '' },
      ],
      results: [hit('u3', 'cy')],
    });

    const row = c['searchRows']()[0];
    expect(row.relation).toBe('incoming');
    expect(row.requestId).toBe('r9');
  });

  it('offers to add a stranger', () => {
    const c = make({ results: [hit('u4', 'di')] });

    const row = c['searchRows']()[0];
    expect(row.relation).toBe('none');
    expect(row.requestId).toBeNull();
  });

  it('splits pending requests by direction', () => {
    const c = make({
      requests: [
        { id: 'r1', userId: 'u1', username: 'ana', status: 'Pending', outgoing: true, createdAt: '' },
        { id: 'r2', userId: 'u2', username: 'bo', status: 'Pending', outgoing: false, createdAt: '' },
      ],
    });

    expect(c['outgoing']().map((r: FriendRequestDto) => r.id)).toEqual(['r1']);
    expect(c['incoming']().map((r: FriendRequestDto) => r.id)).toEqual(['r2']);
  });
});
