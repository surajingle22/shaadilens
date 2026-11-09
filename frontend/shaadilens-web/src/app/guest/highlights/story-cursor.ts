/** Position in the stories viewer: which story (collection) and which photo in it. */
export interface StoryCursor {
  story: number;
  photo: number;
}

/** Next photo; after a story's last photo comes the next story's first. null = everything watched (close). */
export function nextCursor(sizes: readonly number[], c: StoryCursor): StoryCursor | null {
  if (c.photo + 1 < sizes[c.story]) return { story: c.story, photo: c.photo + 1 };
  for (let s = c.story + 1; s < sizes.length; s++) if (sizes[s] > 0) return { story: s, photo: 0 };
  return null;
}

/** Previous photo; from a story's first photo back to the previous story's last. Stays at the very start. */
export function prevCursor(sizes: readonly number[], c: StoryCursor): StoryCursor {
  if (c.photo > 0) return { story: c.story, photo: c.photo - 1 };
  for (let s = c.story - 1; s >= 0; s--) if (sizes[s] > 0) return { story: s, photo: sizes[s] - 1 };
  return c;
}
