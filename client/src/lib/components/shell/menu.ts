import type { IconName } from '$lib/icons/icons';

/** One entry on the application menu. The shortcut is a caption; the binding lives in the shell. */
export interface MenuCommand {
  label: string;
  icon?: IconName;
  shortcut?: string;
  disabled?: boolean;
  danger?: boolean;
  run: () => void;
}

/** A menu bar's worth of commands, grouped as File / View / Account. */
export interface MenuGroup {
  label: string;
  items: MenuCommand[];
}
