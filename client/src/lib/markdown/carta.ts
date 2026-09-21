import { Carta, type Plugin } from 'carta-md';
import DOMPurify from 'dompurify';
import { ATTACHMENT_SCHEME, parseAttachment } from './attachments';

/*
 * The markdown pipeline, in one place.
 *
 * Descriptions and comments are stored as markdown and rendered by Carta — remark and rehype under
 * it — with two rules of this application's own layered on top:
 *
 *   1. Every document is sanitised. Markdown carries raw HTML by definition, and this text is written
 *      by one person and read by their whole team, so it is never trusted on the way out.
 *
 *   2. An uploaded file is referenced as `attachment:{id}` rather than by URL. The bytes sit behind
 *      the issue's own team permission and are fetched with a bearer token, which an <img> cannot
 *      send — so the reference survives into the rendered HTML as a data attribute and is resolved
 *      to a blob at display time. See attachments.ts for that half.
 */

/** Rewrites `attachment:` references before sanitising, which would otherwise drop the scheme. */
const attachmentRefs: Plugin = {
  transformers: [
    {
      execution: 'sync',
      type: 'rehype',
      transform: ({ processor }) => {
        processor.use(() => (tree: unknown) => {
          rewrite(tree as Node);
        });
      }
    }
  ]
};

interface Node {
  type?: string;
  tagName?: string;
  properties?: Record<string, unknown>;
  children?: Node[];
}

/**
 * Moves `attachment:{id}` off `src`/`href` and onto `data-attachment`, and an image's `#size=`
 * choice onto `data-size`, where markdown.css turns it into a width.
 *
 * The attribute goes because nothing should ever request an `attachment:` URL: it is this app's own
 * notation, not a scheme a browser knows. What is left is an element the resolver can find and fill
 * in — and one the sanitiser keeps, because `data-*` survives where an unknown scheme does not.
 */
function rewrite(node: Node): void {
  if (node.type === 'element' && node.properties) {
    const attribute = node.tagName === 'img' ? 'src' : node.tagName === 'a' ? 'href' : null;
    const value = attribute ? node.properties[attribute] : null;

    if (attribute && typeof value === 'string' && value.startsWith(ATTACHMENT_SCHEME)) {
      const { id, size } = parseAttachment(value);

      node.properties.dataAttachment = id;
      if (size && node.tagName === 'img') node.properties.dataSize = size;
      delete node.properties[attribute];
    }
  }

  for (const child of node.children ?? []) rewrite(child);
}

/**
 * One sanitiser for every surface.
 *
 * `data-attachment` is added back because DOMPurify keeps `data-*` on elements but drops attributes
 * it has not been told about once a hook starts inspecting them; links are forced to open in a new
 * tab without handing the opener over.
 */
DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node instanceof HTMLAnchorElement && node.hasAttribute('href')) {
    node.setAttribute('target', '_blank');
    node.setAttribute('rel', 'noopener noreferrer');
  }
});

const sanitizer = (html: string) =>
  DOMPurify.sanitize(html, {
    ADD_ATTR: ['data-attachment', 'data-size', 'target'],
    // Inline frames, forms and event handlers have no business in an issue description, and the
    // default list is otherwise exactly the readable subset this needs.
    FORBID_TAGS: ['iframe', 'form', 'input', 'button', 'style'],
    FORBID_ATTR: ['style']
  });

/** Reading: no upload, because nothing is being written. Shared by every rendered document. */
export const reader = new Carta({ sanitizer, extensions: [attachmentRefs] });


/** Each mounted editor owns its input, selection, history and toolbar state. */
export function createWriter(): Carta {
  return new Carta({ sanitizer, extensions: [attachmentRefs] });
}

