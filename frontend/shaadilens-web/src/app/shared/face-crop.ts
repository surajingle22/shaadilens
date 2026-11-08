/** A face box normalised 0..1, as stored by the API. */
export interface FaceBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * CSS that zooms a photo onto one face — no server-side cropping needed:
 * a square around the face box (1.6× its size) is scaled to fill the element.
 * background-position % = left / (imageWidth − squareSide).
 * Small faces would be a blur in the 400 px thumbnail, so those use the 2048 px web image.
 */
export function faceCropStyle(face: FaceBox, width: number, height: number, thumbUrl: string, webUrl: string): Record<string, string> {
  const W = width, H = height;
  const bw = face.w * W, bh = face.h * H;
  const side = Math.min(Math.max(bw, bh) * 1.6, W, H);
  const left = face.x * W + bw / 2 - side / 2;
  const top = face.y * H + bh / 2 - side / 2;
  const pos = (offset: number, full: number) => (full - side > 0 ? Math.min(100, Math.max(0, (offset / (full - side)) * 100)) : 50);
  const faceInThumbPx = (Math.max(bw, bh) * 400) / Math.max(W, H);
  return {
    'background-image': `url("${faceInThumbPx < 48 ? webUrl : thumbUrl}")`,
    'background-size': `${(W / side) * 100}% ${(H / side) * 100}%`,
    'background-position': `${pos(left, W)}% ${pos(top, H)}%`,
    'background-repeat': 'no-repeat',
  };
}
