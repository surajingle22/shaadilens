import { Photo } from '../../core/api/api.models';
import { groupBursts } from './bursts';

const photo = (id: string, burstGroupId: string | null, isBurstBest = false) => ({ id, burstGroupId, isBurstBest }) as Photo;

describe('groupBursts', () => {
  it('cuts the sorted list into one group per burst, keeping order', () => {
    const groups = groupBursts([photo('a', 'g1', true), photo('b', 'g1'), photo('c', 'g2', true), photo('d', 'g2'), photo('e', 'g2')]);
    expect(groups.map((g) => [g.id, g.photos.map((p) => p.id)])).toEqual([
      ['g1', ['a', 'b']],
      ['g2', ['c', 'd', 'e']],
    ]);
  });

  it('gives photos without a burst their own group', () => {
    expect(groupBursts([photo('x', null), photo('y', null)]).map((g) => g.id)).toEqual(['x', 'y']);
  });
});
