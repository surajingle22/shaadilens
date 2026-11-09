import { nextCursor, prevCursor } from './story-cursor';

describe('story cursor', () => {
  const sizes = [2, 0, 3];

  it('walks through a story, skips empty ones and ends after the last photo', () => {
    expect(nextCursor(sizes, { story: 0, photo: 0 })).toEqual({ story: 0, photo: 1 });
    expect(nextCursor(sizes, { story: 0, photo: 1 })).toEqual({ story: 2, photo: 0 });
    expect(nextCursor(sizes, { story: 2, photo: 2 })).toBeNull();
  });

  it('goes back into the previous story and stays put at the very start', () => {
    expect(prevCursor(sizes, { story: 2, photo: 0 })).toEqual({ story: 0, photo: 1 });
    expect(prevCursor(sizes, { story: 0, photo: 0 })).toEqual({ story: 0, photo: 0 });
  });
});
