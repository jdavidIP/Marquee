import { TestBed } from '@angular/core/testing';
import { IdBadgeComponent } from './id-badge.component';
import { FullProfileDto, LimitedProfileDto } from '../core/models';

describe('IdBadgeComponent', () => {
  function full(overrides: Partial<FullProfileDto> = {}): FullProfileDto {
    return {
      id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      username: 'ana',
      bio: null,
      avatarUrl: null,
      isPrivate: false,
      createdAt: '2026-01-01T00:00:00Z',
      moviesCollected: 3,
      premieresAttended: 18,
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
      avatarUrl: null,
      friendshipStatus: null,
      friendRequestOutgoing: null,
      sharedPremieresAttended: null,
      ...overrides,
    };
  }

  function make(profile: FullProfileDto | LimitedProfileDto) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [IdBadgeComponent] });
    const fixture = TestBed.createComponent(IdBadgeComponent);
    fixture.componentRef.setInput('profile', profile);
    fixture.detectChanges();
    return fixture;
  }

  it('prints the whole card for a full payload, including the access category', () => {
    const fixture = make(full({ premieresAttended: 18 }));
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('ana');
    expect(text).toContain('@ana');
    expect(text).toContain('Industry');
    expect(text).toContain('18');
    expect(fixture.nativeElement.querySelector('.badge--unissued')).toBeNull();
    expect(fixture.nativeElement.querySelector('.badge__blank')).toBeNull();
  });

  it('picks the right rung of the ladder for the boundary cases', () => {
    expect((make(full({ premieresAttended: 4 })).nativeElement as HTMLElement).textContent).toContain(
      'Standby',
    );
    expect((make(full({ premieresAttended: 100 })).nativeElement as HTMLElement).textContent).toContain(
      'Jury',
    );
  });

  it('prints a stable six-digit serial derived from the id, not a stored field', () => {
    const fixture = make(full({ id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301' }));
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toMatch(/No\. \d{6}/);
  });

  it('renders the "unissued" blank-fields state for a limited payload, with the name but no handle', () => {
    const fixture = make(limited({ username: 'stranger' }));
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('stranger');
    expect(el.textContent).not.toContain('@stranger');
    expect(el.querySelector('.badge--unissued')).not.toBeNull();
    expect(el.querySelectorAll('.badge__blank').length).toBe(2);
    expect(el.querySelector('.badge__category')).toBeNull();
    expect(el.querySelector('.badge__footer')).toBeNull();
  });

  it('falls back to a monogram when there is no avatar, on both payload shapes', () => {
    const issued = make(full({ avatarUrl: null }));
    expect(issued.nativeElement.querySelector('.badge__monogram')?.textContent?.trim()).toBe('AN');

    const unissued = make(limited({ username: 'zed', avatarUrl: null }));
    expect(unissued.nativeElement.querySelector('.badge__monogram')?.textContent?.trim()).toBe('ZE');
  });
});
