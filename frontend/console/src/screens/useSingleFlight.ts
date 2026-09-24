import { useRef } from 'react';

/**
 * Double-submit guard (edge 10), shared by every write form. The button is disabled while the
 * mutation is pending, and this ref closes the gap between the first click and React re-rendering
 * the disabled button — two clicks in one frame still send one request.
 */
export function useSingleFlight() {
  const inFlight = useRef(false);
  return {
    begin: () => {
      if (inFlight.current) return false;
      inFlight.current = true;
      return true;
    },
    end: () => {
      inFlight.current = false;
    },
  };
}
