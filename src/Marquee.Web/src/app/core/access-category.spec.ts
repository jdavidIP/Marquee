import { accessCategoryFor, nextAccessCategory } from './access-category';

describe('accessCategoryFor', () => {
  it('lands on Standby for zero attendance', () => {
    expect(accessCategoryFor(0).name).toBe('Standby');
  });

  // Boundaries are inclusive-inclusive (design handoff, issue #59) — the value right below and
  // right at each edge must fall on opposite sides of it.
  it('treats each boundary as inclusive on the lower category, not the next one', () => {
    expect(accessCategoryFor(4).name).toBe('Standby');
    expect(accessCategoryFor(5).name).toBe('General');
    expect(accessCategoryFor(14).name).toBe('General');
    expect(accessCategoryFor(15).name).toBe('Industry');
    expect(accessCategoryFor(39).name).toBe('Industry');
    expect(accessCategoryFor(40).name).toBe('Press');
    expect(accessCategoryFor(99).name).toBe('Press');
    expect(accessCategoryFor(100).name).toBe('Jury');
  });

  it('has no ceiling at Jury', () => {
    expect(accessCategoryFor(100_000).name).toBe('Jury');
  });
});

describe('nextAccessCategory', () => {
  it('steps to the next rung', () => {
    expect(nextAccessCategory(accessCategoryFor(0))!.name).toBe('General');
    expect(nextAccessCategory(accessCategoryFor(99))!.name).toBe('Jury');
  });

  it('is null at Jury — there is nothing after the top', () => {
    expect(nextAccessCategory(accessCategoryFor(100))).toBeNull();
  });
});
