import { afterEach, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import { BlockField } from './BlockField';

// Answer matchMedia for a given reduced-motion state (colour-scheme answers light).
function stubMatchMedia(reducedMotion: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: query.includes('reduced-motion') ? reducedMotion : false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia,
  );
}

// A minimal Canvas 2D context so the field can run its draw path under jsdom (which has no real
// canvas). It records nothing about pixels — the tests assert state and token reads, never output.
function fakeCtx() {
  return {
    setTransform: vi.fn(),
    clearRect: vi.fn(),
    beginPath: vi.fn(),
    roundRect: vi.fn(),
    rect: vi.fn(),
    fill: vi.fn(),
    stroke: vi.fn(),
    save: vi.fn(),
    restore: vi.fn(),
    clip: vi.fn(),
    fillRect: vi.fn(),
    createLinearGradient: vi.fn(() => ({ addColorStop: vi.fn() })),
    fillStyle: '',
    strokeStyle: '',
    globalAlpha: 1,
    lineWidth: 1,
  } as unknown as CanvasRenderingContext2D;
}

function stubGetContext(ctx: CanvasRenderingContext2D | null) {
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(ctx as never);
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('BlockField lifecycle states (edges 1 & 2)', () => {
  it('renders a single static frame with no rAF loop under reduced motion (edge 1)', () => {
    stubMatchMedia(true);
    stubGetContext(fakeCtx());
    const raf = vi.spyOn(window, 'requestAnimationFrame');

    const { container } = render(<BlockField />);
    const canvas = container.querySelector('canvas')!;

    expect(canvas.getAttribute('data-field-state')).toBe('reduced');
    // Reduced motion draws once and never schedules the loop.
    expect(raf).not.toHaveBeenCalled();
  });

  it('degrades to a still, usable field when canvas 2D is unavailable (edge 2)', () => {
    stubMatchMedia(false);
    stubGetContext(null); // getContext('2d') returns null

    const { container } = render(<BlockField />);
    const canvas = container.querySelector('canvas')!;

    // The app stays fully usable: the canvas is present, aria-hidden, and marked degraded.
    expect(canvas.getAttribute('data-field-state')).toBe('degraded');
    expect(canvas.getAttribute('aria-hidden')).toBe('true');
  });

  it('reads its colours from the theme tokens, not hard-coded values (token source)', () => {
    stubMatchMedia(true); // reduced: draws once, no loop — keeps the test finite
    stubGetContext(fakeCtx());

    const requested: string[] = [];
    const realGetComputedStyle = window.getComputedStyle.bind(window);
    vi.spyOn(window, 'getComputedStyle').mockImplementation(((el: Element, pseudo?: string | null) => {
      const style = realGetComputedStyle(el, pseudo ?? undefined);
      return {
        ...style,
        getPropertyValue: (name: string) => {
          requested.push(name);
          return '';
        },
      } as unknown as CSSStyleDeclaration;
    }) as typeof window.getComputedStyle);

    render(<BlockField />);

    // Every field colour is pulled from a `--field-*` theme token.
    for (const token of ['--field-pane', '--field-active', '--field-edge', '--field-reflection', '--field-pulse']) {
      expect(requested).toContain(token);
    }
  });
});
