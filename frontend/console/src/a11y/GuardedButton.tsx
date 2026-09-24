import { useId, type ButtonHTMLAttributes } from 'react';

export interface GuardedButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /**
   * Why the viewer may not use this control right now (`Requires deals.write`, "Held for lead
   * approval…"), or null/undefined when they may. A reason always disables the button.
   */
  deniedReason?: string | null;
}

/**
 * The console's one "disabled, not hidden" write control (010-P8). A denied control:
 *
 * - stays **visible**, so every role sees what Aperture does and what they would need;
 * - is natively `disabled`, so it is **not in the tab order** — the same rule `Navigation.tsx`
 *   applies to a denied section (no href, never focusable);
 * - keeps its **action label as its accessible name** ("New deal", not the tooltip), and carries the
 *   reason as an **accessible description** through a visually hidden node it points to with
 *   `aria-describedby` — `title` is not used, because a browser may promote it to the name;
 * - shows the same reason to sighted users as a hover tip (`data-denied-reason`, see `styles.css`).
 *
 * Never enforcement: the API denies regardless.
 */
export function GuardedButton({
  deniedReason,
  disabled,
  'aria-describedby': describedBy,
  children,
  ...rest
}: GuardedButtonProps) {
  const reasonId = useId();
  const denied = typeof deniedReason === 'string' && deniedReason.length > 0;
  const describedByIds = [describedBy, denied ? reasonId : undefined].filter(Boolean).join(' ');

  return (
    <>
      <button
        type="button"
        {...rest}
        disabled={denied || disabled}
        aria-describedby={describedByIds.length > 0 ? describedByIds : undefined}
        data-denied-reason={denied ? deniedReason : undefined}
      >
        {children}
      </button>
      {denied && (
        <span id={reasonId} className="visually-hidden">
          {deniedReason}
        </span>
      )}
    </>
  );
}
