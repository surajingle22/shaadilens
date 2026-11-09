import { Photo } from '../../core/api/api.models';

export interface Burst {
  id: string;
  photos: Photo[];
}

/**
 * The Duplicates list arrives sorted by burst, keeper first. Cut it into consecutive groups
 * (one row per burst in the UI). A photo without a burst id gets a group of its own.
 */
export function groupBursts(photos: readonly Photo[]): Burst[] {
  const groups: Burst[] = [];
  for (const p of photos) {
    const last = groups.at(-1);
    if (last && p.burstGroupId && last.id === p.burstGroupId) last.photos.push(p);
    else groups.push({ id: p.burstGroupId ?? p.id, photos: [p] });
  }
  return groups;
}
