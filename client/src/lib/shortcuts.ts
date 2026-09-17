/*
 * Keyboard shortcuts, written the way they are read.
 *
 * A shortcut is a string: `mod+k`, `shift+c`, `f5`, `?`, or a sequence of two chords separated by a
 * space, `g i`. `mod` is Ctrl, or ⌘ on a Mac, so one binding serves both keyboards.
 *
 * Which keys are worth binding is decided by the browser, not by taste. Ctrl+N, Ctrl+Shift+N, Ctrl+T,
 * Ctrl+W and Ctrl+1…9 never reach a page at all — the browser opens its window or switches its tab
 * before any script sees the key. So the application's verbs are single keys and `g` sequences, the
 * way a web tracker binds them, and only keys a page is actually handed carry a modifier.
 */

export interface Chord {
  key: string;
  mod: boolean;
  shift: boolean;
  alt: boolean;
}

export type Sequence = Chord[];

const MODIFIERS = new Set(['Control', 'Meta', 'Shift', 'Alt', 'AltGraph', 'CapsLock']);

export const isMac =
  typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

export function parse(shortcut: string): Sequence {
  return shortcut
    .trim()
    .split(/\s+/)
    .map((part) => {
      // `+` is a separator, except as the last key of a chord: `mod++`.
      const tokens = part.toLowerCase().split(/\+(?!$)/);
      const key = tokens.pop()!;

      return {
        key,
        mod: tokens.includes('mod'),
        shift: tokens.includes('shift'),
        alt: tokens.includes('alt')
      };
    });
}

/** True for a key that no modifier and no function key guards — one that typing would produce. */
export function isPrintable(chord: Chord): boolean {
  return !chord.mod && !chord.alt && chord.key.length === 1;
}

/** A modifier on its own is never a chord; it is the start of one. */
export function isModifierOnly(event: KeyboardEvent): boolean {
  return MODIFIERS.has(event.key);
}

export function matches(chord: Chord, event: KeyboardEvent): boolean {
  if (chord.mod !== (event.ctrlKey || event.metaKey)) return false;
  if (chord.alt !== event.altKey) return false;

  // Punctuation is reached through Shift on most layouts — `?` is Shift+/ — so for those the character
  // is the whole of the binding and Shift is not asked about.
  const punctuation = chord.key.length === 1 && !/^[a-z0-9]$/.test(chord.key);
  if (!punctuation && chord.shift !== event.shiftKey) return false;

  return event.key.toLowerCase() === chord.key;
}

const NAMES: Record<string, string> = {
  enter: 'Enter',
  escape: 'Esc',
  arrowup: '↑',
  arrowdown: '↓',
  arrowleft: '←',
  arrowright: '→',
  ' ': 'Space'
};

/** The keys of one chord as captions: `mod+shift+k` is ["Ctrl", "Shift", "K"]. */
export function chordKeys(chord: Chord): string[] {
  const keys: string[] = [];

  if (chord.mod) keys.push(isMac ? '⌘' : 'Ctrl');
  if (chord.alt) keys.push(isMac ? '⌥' : 'Alt');
  if (chord.shift) keys.push(isMac ? '⇧' : 'Shift');

  keys.push(NAMES[chord.key] ?? chord.key.toUpperCase());
  return keys;
}

/** One line of text for a tooltip: "New issue (C)", "Go to board (G then B)". */
export function describe(shortcut: string): string {
  return parse(shortcut)
    .map((chord) => chordKeys(chord).join(isMac ? '' : '+'))
    .join(' then ');
}
