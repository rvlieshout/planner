import { attachments as attachmentsApi } from '$lib/api';
import type { AttachmentDto, Guid } from '$lib/api/types';

/*
 * Turning `attachment:{id}` into something a browser will display.
 *
 * The bytes of an uploaded file are behind the issue's team permission and are asked for with a
 * bearer token. An <img> cannot send one — it is the browser making that request, not this app — so
 * a rendered document carries the reference as `data-attachment` and this fills in the `src` from a
 * blob fetched the ordinary way.
 *
 * Blobs are cached per attachment for the session: the same screenshot is usually on screen in the
 * description and in the attachment list at once, and an issue is reopened all day. The cache holds
 * promises rather than URLs so that two images of the same file share one request, and it is thrown
 * away on sign-out rather than on unmount, because a revoked URL cannot be brought back.
 */

/** How an uploaded file is referenced inside markdown. */
export const ATTACHMENT_SCHEME = 'attachment:';

/*
 * An image pasted into a description or a comment is a real attachment on the issue from the moment
 * it is uploaded — before anything is saved — so the page's attachment list has to hear about it.
 * The editors that upload sit a few components below the page that holds the list, so they announce
 * here rather than hand a callback down through every layer in between.
 */
type UploadListener = (issueId: Guid, attachment: AttachmentDto) => void;

const uploadListeners = new Set<UploadListener>();

export function announceUpload(issueId: Guid, attachment: AttachmentDto): void {
  for (const listener of uploadListeners) listener(issueId, attachment);
}

/** Subscribes to uploads made from any editor. Returns the unsubscribe function. */
export function onUpload(listener: UploadListener): () => void {
  uploadListeners.add(listener);
  return () => uploadListeners.delete(listener);
}

/*
 * How large an image is drawn, picked in the editor and kept in the reference itself as
 * `attachment:{id}#size=m` — plain markdown, so it survives copy and paste, shows in the source and
 * is undone like any other edit. No size means the default: the column's width, capped in height.
 */
export const IMAGE_SIZES = ['s', 'm', 'l', 'full'] as const;
export type ImageSize = (typeof IMAGE_SIZES)[number];

const SIZE_FRAGMENT = '#size=';

export function parseAttachment(reference: string): { id: Guid; size: ImageSize | null } {
  const [id, fragment = ''] = reference.slice(ATTACHMENT_SCHEME.length).split('#', 2);
  const size = fragment.startsWith('size=') ? fragment.slice('size='.length) : '';

  return { id, size: (IMAGE_SIZES as readonly string[]).includes(size) ? (size as ImageSize) : null };
}

/**
 * Sets the size of the `occurrence`-th image of attachment `id` in a markdown source, returning the
 * span to replace — or null when that image is not there any more.
 *
 * By occurrence rather than by id alone, because the same screenshot can sit in a document twice and
 * resizing one of them must not resize the other.
 */
export function resizeReference(
  source: string,
  id: Guid,
  occurrence: number,
  size: ImageSize | null
): { start: number; end: number; text: string } | null {
  const escaped = id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const pattern = new RegExp(`!\\[(?:\\\\.|[^\\]\\\\])*\\]\\(${ATTACHMENT_SCHEME}${escaped}(#[^)\\s]*)?`, 'g');

  let index = 0;
  for (const match of source.matchAll(pattern)) {
    if (index++ !== occurrence) continue;

    const withoutSize = match[0].slice(0, match[0].length - (match[1]?.length ?? 0));
    const text = size ? `${withoutSize}${SIZE_FRAGMENT}${size}` : withoutSize;

    return { start: match.index, end: match.index + match[0].length, text };
  }

  return null;
}

const cache = new Map<Guid, Promise<string>>();

function objectUrl(id: Guid): Promise<string> {
  const existing = cache.get(id);
  if (existing) return existing;

  const pending = attachmentsApi
    .download(id)
    .then((blob) => URL.createObjectURL(blob))
    .catch((error: unknown) => {
      // Not kept: an attachment that failed once because the network blinked should be asked for
      // again the next time it is on screen. One that is genuinely gone fails again, cheaply.
      cache.delete(id);
      throw error;
    });

  cache.set(id, pending);
  return pending;
}

/** Called on sign-out. Every URL is revoked, or the blobs stay in memory until the tab closes. */
export function revokeAttachments(): void {
  for (const pending of cache.values()) {
    void pending.then(URL.revokeObjectURL).catch(() => {});
  }

  cache.clear();
}

/**
 * Fills in every attachment reference inside one rendered document.
 *
 * Images get a `src`; links get a click that saves the file under its own name, the same way the
 * attachment list does. A reference whose attachment has been deleted is marked rather than left as
 * a broken image — "this was here and is not any more" is the honest thing to show.
 */
export function resolveAttachments(root: HTMLElement): void {
  for (const image of root.querySelectorAll<HTMLImageElement>('img[data-attachment]')) {
    if (image.dataset.resolved) continue;
    image.dataset.resolved = 'pending';

    const id = image.dataset.attachment!;

    void objectUrl(id)
      .then((url) => {
        image.src = url;
        image.dataset.resolved = 'done';
      })
      .catch(() => {
        image.dataset.resolved = 'missing';
        image.replaceWith(placeholder(image.alt));
      });
  }

  for (const link of root.querySelectorAll<HTMLAnchorElement>('a[data-attachment]')) {
    if (link.dataset.resolved) continue;
    link.dataset.resolved = 'done';

    link.href = '#';
    link.addEventListener('click', async (event) => {
      event.preventDefault();

      try {
        await save(link.dataset.attachment!, link.textContent?.trim() || 'attachment');
      } catch {
        link.replaceWith(placeholder(link.textContent ?? ''));
      }
    });
  }
}

async function save(id: Guid, fileName: string): Promise<void> {
  const url = await objectUrl(id);

  // A synthetic anchor is the only way a browser saves a blob under a chosen name.
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
}

function placeholder(label: string): HTMLElement {
  const span = document.createElement('span');
  span.className = 'attachment-missing';
  span.textContent = label ? `${label} — attachment unavailable` : 'Attachment unavailable';

  return span;
}
