import type { IconName } from '$lib/icons/icons';

/**
 * One row of a property picker.
 *
 * A chip is its own label, so an option carries whatever makes it recognisable at a glance: a glyph
 * for a status or a priority, a monogram for a person, a swatch for a project's colour.
 */
export interface SelectOption<T> {
  value: T;
  label: string;
  icon?: IconName;
  /** Colours the icon and the swatch. Anything CSS accepts, including a token reference. */
  color?: string;
  /** A monogram instead of an icon — assignees and leads. */
  avatarName?: string;
  /** A different seed for the monogram's colour — an email, so a rename keeps the same colour. */
  avatarSeed?: string;
  hint?: string;
  disabled?: boolean;
}
