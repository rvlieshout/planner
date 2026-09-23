import type { IconName } from '$lib/icons/icons';
import { isModifierOnly, isPrintable, matches, parse, type Chord } from '$lib/shortcuts';

/*
 * Everything the application can be told to do, in one list.
 *
 * The shell contributes what is always there — new issue, go to the board, hide the sidebar — and the
 * open page contributes its own through `chrome`: save this issue, add a sub-issue. The application
 * menu, the command palette and the keyboard all read that one list, so a command that is listed is a
 * command that works, and a shortcut that is captioned is a shortcut that is bound.
 */

export interface Command {
  label: string;
  icon?: IconName;
  /** In `shortcuts.ts` notation: `c`, `shift+c`, `mod+s`, `g b`. */
  shortcut?: string;
  /** Other words someone might search the palette for. */
  keywords?: string[];
  disabled?: boolean;
  danger?: boolean;
  run: () => void;
}

/** A menu bar's worth of commands: File, Go to, View, or the open page's own. */
export interface CommandGroup {
  label: string;
  items: Command[];
}

/** How long the second key of `g b` is waited for. */
const SEQUENCE_TIMEOUT_MS = 1500;

const sameChord = (a: Chord, b: Chord) =>
  a.key === b.key && a.mod === b.mod && a.shift === b.shift && a.alt === b.alt;

function isTyping(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null;
  return Boolean(
    element?.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(element?.tagName ?? '')
  );
}

/** A dialog that offers commands of its own while it is open — the issue editor's Save, say. */
export interface ModalCommands {
  element: HTMLElement;
  groups: CommandGroup[];
}

class Commands {
  paletteOpen = $state(false);

  /** While set, its commands stand in for the page's: the page behind a modal is not what a key means. */
  modal = $state<ModalCommands | null>(null);

  /** The first key of a sequence, while the second is still expected. */
  #pending: { chord: Chord; at: number } | null = null;

  openPalette(): void {
    this.paletteOpen = true;
  }

  closePalette(): void {
    this.paletteOpen = false;
  }

  /**
   * Runs the command a key press is bound to, if any.
   *
   * A printable key never fires while a field has focus, or a shortcut would eat what is being typed;
   * a chord with Ctrl or a function key does, because typing cannot produce one. Nothing fires while a
   * modal is open — the dialog owns the keyboard, and the command would act on the page behind it —
   * unless the key was pressed in the modal that published `modal`, whose commands `groups` then are.
   */
  handle(event: KeyboardEvent, groups: CommandGroup[]): void {
    if (event.defaultPrevented || event.isComposing || event.repeat || isModifierOnly(event)) return;

    const pending =
      this.#pending && Date.now() - this.#pending.at < SEQUENCE_TIMEOUT_MS ? this.#pending.chord : null;
    this.#pending = null;

    // Focus is trapped in the topmost modal, so the dialog the key came from is the one in charge.
    // A confirmation raised over the editor, or the palette itself, is not the editor.
    const owner =
      (event.target instanceof Element ? event.target.closest('dialog[open]') : null) ??
      document.querySelector('dialog[open]');
    if (owner && owner !== this.modal?.element) return;

    const typing = isTyping(event.target);
    const bound = groups
      .flatMap((group) => group.items)
      .filter((command) => command.shortcut)
      .map((command) => ({ command, sequence: parse(command.shortcut!) }))
      .filter(({ sequence }) => !(typing && isPrintable(sequence[0])));

    const hit = pending
      ? bound.find(
          ({ sequence }) =>
            sequence.length === 2 && sameChord(sequence[0], pending) && matches(sequence[1], event)
        )
      : bound.find(({ sequence }) => sequence.length === 1 && matches(sequence[0], event));

    if (hit) {
      // A disabled command still owns its key: Ctrl+S with nothing to save must not open the
      // browser's "Save page as" instead.
      event.preventDefault();
      if (!hit.command.disabled) hit.command.run();
      return;
    }

    const opener = bound.find(({ sequence }) => sequence.length === 2 && matches(sequence[0], event));
    if (opener) {
      event.preventDefault();
      this.#pending = { chord: opener.sequence[0], at: Date.now() };
    }
  }
}

export const commands = new Commands();
