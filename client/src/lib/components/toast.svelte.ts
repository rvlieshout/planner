/*
 * Transient notices — a save that worked, a background action that did not.
 *
 * Deliberately not where validation errors go: a message about a field belongs beside that field,
 * and a toast that vanishes after four seconds is the wrong place for anything the user must act on.
 * This is for outcomes of things they have already finished doing.
 */

export type ToastKind = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

const DURATIONS: Record<ToastKind, number> = {
  success: 3_000,
  info: 4_000,
  // Longer: a failure is the one a user has to finish reading before deciding what to do about it.
  error: 7_000
};

class Toasts {
  items = $state<Toast[]>([]);

  #nextId = 1;

  show(kind: ToastKind, message: string): void {
    const id = this.#nextId++;
    this.items = [...this.items, { id, kind, message }];

    setTimeout(() => this.dismiss(id), DURATIONS[kind]);
  }

  success(message: string): void {
    this.show('success', message);
  }

  error(message: string): void {
    this.show('error', message);
  }

  info(message: string): void {
    this.show('info', message);
  }

  dismiss(id: number): void {
    this.items = this.items.filter((toast) => toast.id !== id);
  }
}

export const toasts = new Toasts();
