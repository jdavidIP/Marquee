import { TestBed } from '@angular/core/testing';
import { EmblemTicketComponent } from './emblem-ticket.component';

describe('EmblemTicketComponent', () => {
  function make(tier: number) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [EmblemTicketComponent] });
    const fixture = TestBed.createComponent(EmblemTicketComponent);
    fixture.componentRef.setInput('tier', tier);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  it('names each tier its material, one per rung of the ladder', () => {
    expect(make(1)['material']().name).toBe('Paper');
    expect(make(2)['material']().name).toBe('Bronze');
    expect(make(3)['material']().name).toBe('Silver');
    expect(make(4)['material']().name).toBe('Gold');
    expect(make(5)['material']().name).toBe('Platinum');
  });

  it('falls back to Paper for a tier outside 1-5 rather than rendering blank', () => {
    expect(make(0)['material']().name).toBe('Paper');
    expect(make(6)['material']().name).toBe('Paper');
  });

  it('renders the tier digit and material name in the DOM', () => {
    const fixture = TestBed.createComponent(EmblemTicketComponent);
    fixture.componentRef.setInput('tier', 4);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('4');
    expect(text).toContain('Gold');
  });
});
