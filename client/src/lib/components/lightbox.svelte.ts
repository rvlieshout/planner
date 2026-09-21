/*
 * One image, shown as large as the screen allows.
 *
 * Opened from anything that shows an image — a description, a comment — and drawn by LightboxHost
 * in the root layout, so every document gets the same viewer without mounting its own dialog.
 */

export interface LightboxImage {
  src: string;
  alt: string;
}

class Lightbox {
  current = $state<LightboxImage | null>(null);

  open(image: LightboxImage): void {
    this.current = image;
  }

  close(): void {
    this.current = null;
  }
}

export const lightbox = new Lightbox();
