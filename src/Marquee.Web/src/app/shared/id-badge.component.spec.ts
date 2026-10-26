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

  describe('photo controls (#63)', () => {
    function editable(profile: FullProfileDto) {
      const fixture = make(profile);
      fixture.componentRef.setInput('editable', true);
      fixture.detectChanges();
      return fixture;
    }

    const menuItems = (el: HTMLElement) =>
      [...el.querySelectorAll('.badge__menu button')].map((b) => b.textContent?.trim());

    it("offers nothing on someone else's badge", () => {
      const el = make(full({ avatarUrl: 'https://x.test/a.webp' })).nativeElement as HTMLElement;

      expect(el.querySelector('.badge__portrait button, button.badge__portrait')).toBeNull();
      expect(el.textContent).not.toContain('Change');
    });

    it('offers only to add a photo when there is none', () => {
      const fixture = editable(full({ avatarUrl: null }));
      const el = fixture.nativeElement as HTMLElement;

      (el.querySelector('button.badge__portrait') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(menuItems(el)).toEqual(['Add photo']);
    });

    it('offers to change or remove an existing photo, and says which was picked', () => {
      const fixture = editable(full({ avatarUrl: 'https://x.test/a.webp' }));
      const el = fixture.nativeElement as HTMLElement;
      let changed = 0;
      let removed = 0;
      fixture.componentInstance.changePhoto.subscribe(() => changed++);
      fixture.componentInstance.removePhoto.subscribe(() => removed++);

      (el.querySelector('button.badge__portrait') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(menuItems(el)).toEqual(['Change photo', 'Remove photo']);

      (el.querySelectorAll('.badge__menu button')[1] as HTMLButtonElement).click();
      fixture.detectChanges();

      expect([changed, removed]).toEqual([0, 1]);
      expect(el.querySelector('.badge__menu')).toBeNull();
    });

    it('closes on Escape and hands focus back to the portrait', () => {
      const fixture = editable(full({ avatarUrl: 'https://x.test/a.webp' }));
      document.body.appendChild(fixture.nativeElement);
      const el = fixture.nativeElement as HTMLElement;
      const portrait = el.querySelector('button.badge__portrait') as HTMLButtonElement;
      portrait.click();
      fixture.detectChanges();
      const first = el.querySelector('.badge__menu button') as HTMLButtonElement;
      first.focus();

      first.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();

      expect(el.querySelector('.badge__menu')).toBeNull();
      expect(document.activeElement).toBe(portrait);
      fixture.nativeElement.remove();
    });

    it('closes when something outside it is clicked', () => {
      const fixture = editable(full({ avatarUrl: null }));
      const el = fixture.nativeElement as HTMLElement;
      (el.querySelector('button.badge__portrait') as HTMLButtonElement).click();
      fixture.detectChanges();

      document.body.click();
      fixture.detectChanges();

      expect(el.querySelector('.badge__menu')).toBeNull();
    });

    it('uses plain buttons, not the ARIA menu roles it has no arrow-key support for', () => {
      const fixture = editable(full({ avatarUrl: null }));
      const el = fixture.nativeElement as HTMLElement;
      (el.querySelector('button.badge__portrait') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(el.querySelector('[role="menu"], [role="menuitem"], [aria-haspopup]')).toBeNull();
    });
  });
});
