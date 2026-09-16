/*
 * Asking the user a yes/no question from code that has no view.
 *
 * A view model knows *when* to ask — leaving a page with unsaved work, deleting a milestone — and
 * only the layer above it can put a dialog on the screen. `confirm.ask()` is that seam: it resolves
 * to the answer, and ConfirmHost in the root layout is the thing that renders it.
 *
 * The safe answer is always the default one. Enter, Escape and the backdrop all take it, so only a
 * deliberate click on the other button does the irreversible thing.
 */

export interface ConfirmOptions {
  title: string;
  /** What happens, in the words the user would use. "The milestone goes; its issues stay." */
  message: string;
  /** The button that does the thing. Defaults to "Discard". */
  confirmLabel?: string;
  /** The button that does not. Defaults to "Keep editing", and is always the default button. */
  cancelLabel?: string;
  /** Paints the confirming button as destructive. */
  danger?: boolean;
}

interface Pending extends ConfirmOptions {
  resolve: (answer: boolean) => void;
}

class Confirm {
  current = $state<Pending | null>(null);

  ask(options: ConfirmOptions): Promise<boolean> {
    // A second question while one is open would replace it and leave the first caller waiting for
    // ever. Answering "no" is the safe resolution of a question nobody can see.
    this.current?.resolve(false);

    return new Promise<boolean>((resolve) => {
      this.current = { ...options, resolve };
    });
  }

  answer(value: boolean): void {
    const pending = this.current;
    this.current = null;
    pending?.resolve(value);
  }

  /**
   * The unsaved-work guard, in one call. `summary` says what stands to be lost, in the words the
   * prompt will use — "the issue's description and 2 unposted comments".
   */
  discard(summary: string): Promise<boolean> {
    return this.ask({
      title: 'Discard unsaved changes?',
      message: `Leaving this page discards ${summary}.`,
      confirmLabel: 'Discard',
      cancelLabel: 'Keep editing',
      danger: true
    });
  }
}

export const confirm = new Confirm();
