import { useEffect, useRef, useState } from 'react';
import {
  buildGrid,
  flowAt,
  pulseAlive,
  pulseValue,
  shouldAnimate,
  type Grid,
  type Pointer,
  type Pulse,
} from './fieldMath';

// The reactive canvas backdrop. It renders a grid of blocks behind the console shell that flow away
// from the pointer and ripple on click, staying quiet-but-alive: low amplitude, no grain. All the
// motion math lives in the pure `blockField.ts` module; this component owns only the DOM plumbing —
// one requestAnimationFrame loop, a pointer ref (never React state — re-rendering hundreds of
// blocks per frame is the bug this design avoids), the pulse list, and the lifecycle gates.
//
// `data-field-state` is the observable contract (see plan §Observability): a browser check and a
// test can read `animating` / `reduced` / `degraded` without inspecting pixels.
type FieldState = 'animating' | 'reduced' | 'degraded';

const PULSE_DURATION = 1100;

interface ThemeColours {
  block: string;
  active: string;
  pulse: string;
}

// Read the field's colours from the CSS theme tokens so it is correct in whichever theme is active.
// P1 introduces these token names; P2 restructures the token sets and adds the light theme. Falling
// back to the current dark values keeps a partial rollout rendering.
function readThemeColours(el: Element): ThemeColours {
  const s = getComputedStyle(el);
  const pick = (name: string, fallback: string) => {
    const v = s.getPropertyValue(name).trim();
    return v.length > 0 ? v : fallback;
  };
  return {
    block: pick('--field-block', 'rgba(76, 141, 255, 0.05)'),
    active: pick('--field-block-active', 'rgba(76, 141, 255, 0.28)'),
    pulse: pick('--field-pulse', 'rgba(120, 180, 255, 0.6)'),
  };
}

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
}

export function BlockField(): React.ReactElement {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [state, setState] = useState<FieldState>(prefersReducedMotion() ? 'reduced' : 'animating');

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) {
      // Canvas 2D unavailable: degrade silently, the app still renders (edge 2).
      setState('degraded');
      return;
    }

    const reduced = prefersReducedMotion();
    let colours = readThemeColours(canvas);
    let grid: Grid = { cols: 0, rows: 0, cell: 0, blocks: [] };
    let dpr = Math.min(window.devicePixelRatio || 1, 2);
    let width = 0;
    let height = 0;
    const pointer: { current: Pointer | null } = { current: null };
    const pulses: Pulse[] = [];
    let rafId: number | null = null;

    function resize() {
      width = canvas!.clientWidth;
      height = canvas!.clientHeight;
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas!.width = Math.max(1, Math.round(width * dpr));
      canvas!.height = Math.max(1, Math.round(height * dpr));
      ctx!.setTransform(dpr, 0, 0, dpr, 0, 0);
      grid = buildGrid(width, height);
      colours = readThemeColours(canvas!);
    }

    function draw(now: number) {
      ctx!.clearRect(0, 0, width, height);
      // Drop expired pulses so the list cannot grow unbounded.
      for (let i = pulses.length - 1; i >= 0; i--) {
        const p = pulses[i]!;
        if (!pulseAlive(p.start, now, PULSE_DURATION)) pulses.splice(i, 1);
      }

      for (const block of grid.blocks) {
        const flow = flowAt(block.x, block.y, pointer.current);
        let intensity = flow.intensity;
        for (const p of pulses) {
          const dist = Math.hypot(block.x - p.x, block.y - p.y);
          intensity = Math.max(intensity, pulseValue(now - p.start, dist));
        }
        const size = block.size - 2;
        // Blocks flow *in and out*: a subtle scale + displacement, brightening with intensity.
        const scale = 1 + intensity * 0.18;
        const s = size * scale;
        const x = block.x + flow.dx - s / 2;
        const y = block.y + flow.dy - s / 2;
        ctx!.fillStyle = intensity > 0.001 ? colours.active : colours.block;
        ctx!.globalAlpha = 0.35 + intensity * 0.65;
        ctx!.fillRect(x, y, s, s);
      }
      ctx!.globalAlpha = 1;
    }

    function frame(now: number) {
      draw(now);
      schedule();
    }

    function schedule() {
      const hidden = typeof document !== 'undefined' && document.hidden;
      if (shouldAnimate({ hidden, reducedMotion: reduced })) {
        rafId = requestAnimationFrame(frame);
      } else {
        rafId = null;
      }
    }

    function onVisibility() {
      if (reduced) return;
      if (document.hidden) {
        if (rafId !== null) {
          cancelAnimationFrame(rafId);
          rafId = null;
        }
      } else if (rafId === null) {
        schedule();
      }
    }

    function onPointerMove(e: PointerEvent) {
      const rect = canvas!.getBoundingClientRect();
      pointer.current = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    }

    function onPointerLeave() {
      pointer.current = null;
    }

    function onPointerDown(e: PointerEvent) {
      const rect = canvas!.getBoundingClientRect();
      pulses.push({ x: e.clientX - rect.left, y: e.clientY - rect.top, start: performance.now() });
      // Under reduced motion the loop never runs, so a pulse would not paint — that is intended:
      // reduced motion means no ripple.
    }

    resize();

    if (reduced) {
      // A single static frame, no loop (edge 1). The field still reads the theme tokens so it is
      // correct in both themes even when still.
      setState('reduced');
      draw(performance.now());
    } else {
      setState('animating');
      // The whole window is the interaction surface — listen on window so the field reacts even
      // though the shell UI sits above it (the canvas itself is pointer-events:none).
      window.addEventListener('pointermove', onPointerMove);
      window.addEventListener('pointerdown', onPointerDown);
      window.addEventListener('pointerleave', onPointerLeave);
      document.addEventListener('visibilitychange', onVisibility);
      schedule();
    }

    const resizeObserver = new ResizeObserver(() => resize());
    resizeObserver.observe(canvas);

    return () => {
      if (rafId !== null) cancelAnimationFrame(rafId);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerdown', onPointerDown);
      window.removeEventListener('pointerleave', onPointerLeave);
      document.removeEventListener('visibilitychange', onVisibility);
      resizeObserver.disconnect();
    };
  }, []);

  return (
    <canvas
      ref={canvasRef}
      className="field"
      data-field-state={state}
      aria-hidden="true"
    />
  );
}
