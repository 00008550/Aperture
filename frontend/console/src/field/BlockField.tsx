import { useEffect, useRef, useState } from 'react';
import {
  buildPaneGrid,
  idleShimmer,
  pulseAlive,
  pulseValue,
  refractAt,
  shouldAnimate,
  type Grid,
  type Pointer,
  type Pulse,
} from './fieldMath';

// The reactive canvas backdrop — Aurora Glass form (010-P2). It renders a grid of big connected
// rounded glass panes behind the console shell: near the pointer the panes bulge/lean/refract
// toward the cursor, catch a diagonal reflection streak clipped inside each pane, and light their
// edges as they wake; a click fires an impulse ripple. Quiet at rest (a slow idle shimmer), lively
// near the cursor — "quiet but alive". All the motion math lives in the pure `fieldMath.ts` module;
// this component owns only the DOM plumbing — one requestAnimationFrame loop, a pointer ref (never
// React state — re-rendering hundreds of panes per frame is the bug this design avoids), the pulse
// list, and the lifecycle gates.
//
// `data-field-state` is the observable contract (see plan §Observability): a browser check and a
// test can read `animating` / `reduced` / `degraded` without inspecting pixels.
type FieldState = 'animating' | 'reduced' | 'degraded';

const PULSE_DURATION = 1100;

interface ThemeColours {
  pane: string;
  active: string;
  edge: string;
  reflection: string;
  pulse: string;
}

// Read the field's colours from the CSS theme tokens so it is correct in whichever theme is active.
// P2 restructured these into the Aurora Glass light + dark sets; falling back to sane dark values
// keeps a partial rollout rendering.
function readThemeColours(el: Element): ThemeColours {
  const s = getComputedStyle(el);
  const pick = (name: string, fallback: string) => {
    const v = s.getPropertyValue(name).trim();
    return v.length > 0 ? v : fallback;
  };
  return {
    pane: pick('--field-pane', 'rgba(230, 236, 246, 0.05)'),
    active: pick('--field-active', 'rgba(65, 214, 195, 0.30)'),
    edge: pick('--field-edge', 'rgba(138, 155, 255, 0.60)'),
    reflection: pick('--field-reflection', 'rgba(255, 255, 255, 0.28)'),
    pulse: pick('--field-pulse', 'rgba(65, 214, 195, 0.55)'),
  };
}

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
}

// A rounded-rect path helper — panes read as one glass surface, so the corners are generous but the
// seams between them stay thin (the grid's gap). Uses the native roundRect where present.
function panePath(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, r: number): void {
  ctx.beginPath();
  if (typeof ctx.roundRect === 'function') {
    ctx.roundRect(x, y, w, w, r);
  } else {
    ctx.rect(x, y, w, w);
  }
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
      grid = buildPaneGrid(width, height);
      colours = readThemeColours(canvas!);
    }

    function drawPane(block: Grid['blocks'][number], now: number) {
      const refr = refractAt(block.x, block.y, pointer.current);
      let intensity = refr.intensity;
      for (const p of pulses) {
        const dist = Math.hypot(block.x - p.x, block.y - p.y);
        intensity = Math.max(intensity, pulseValue(now - p.start, dist));
      }
      const idle = reduced ? 0 : idleShimmer(block.col, block.row, now);
      const lit = Math.max(idle, intensity);

      // Bulge toward the cursor: a subtle scale + lean displacement.
      const edge = block.size;
      const radius = edge * 0.28;
      const scale = 1 + intensity * 0.12;
      const s = edge * scale;
      const cx = block.x + refr.dx;
      const cy = block.y + refr.dy;
      const x = cx - s / 2;
      const y = cy - s / 2;

      // Base pane fill — the quiet glass surface.
      panePath(ctx!, x, y, s, radius);
      ctx!.globalAlpha = 0.5 + idle * 2;
      ctx!.fillStyle = colours.pane;
      ctx!.fill();

      // Wake tint — accent glass brightening as the pane leans toward the pointer / a pulse passes.
      if (intensity > 0.01) {
        ctx!.globalAlpha = intensity * 0.9;
        ctx!.fillStyle = colours.active;
        ctx!.fill();
      }

      // Edge light — the pane's rim catches light as it wakes.
      if (lit > 0.06) {
        ctx!.globalAlpha = Math.min(1, lit * 1.1);
        ctx!.lineWidth = 1;
        ctx!.strokeStyle = colours.edge;
        ctx!.stroke();
      }

      // Diagonal reflection streak, clipped inside the pane — only where the pane is awake, to keep
      // the whole sheet at 60fps.
      if (lit > 0.14) {
        ctx!.save();
        panePath(ctx!, x, y, s, radius);
        ctx!.clip();
        const g = ctx!.createLinearGradient(x, y, x + s, y + s);
        const stop = colours.reflection;
        g.addColorStop(0.0, 'transparent');
        g.addColorStop(0.42, 'transparent');
        g.addColorStop(0.5, stop);
        g.addColorStop(0.58, 'transparent');
        g.addColorStop(1.0, 'transparent');
        ctx!.globalAlpha = Math.min(1, lit);
        ctx!.fillStyle = g;
        ctx!.fillRect(x, y, s, s);
        ctx!.restore();
      }
    }

    function draw(now: number) {
      ctx!.clearRect(0, 0, width, height);
      // Drop expired pulses so the list cannot grow unbounded.
      for (let i = pulses.length - 1; i >= 0; i--) {
        const p = pulses[i]!;
        if (!pulseAlive(p.start, now, PULSE_DURATION)) pulses.splice(i, 1);
      }
      for (const block of grid.blocks) drawPane(block, now);
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

    // The theme can flip at runtime (the sidebar toggle sets `data-theme` on <html>). Re-read the
    // tokens when it does so the field is correct in the new theme without a reload. Under reduced
    // motion there is no loop, so repaint the single static frame immediately.
    const themeObserver = new MutationObserver(() => {
      colours = readThemeColours(canvas);
      if (reduced) draw(performance.now());
    });
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    const resizeObserver = new ResizeObserver(() => resize());
    resizeObserver.observe(canvas);

    return () => {
      if (rafId !== null) cancelAnimationFrame(rafId);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerdown', onPointerDown);
      window.removeEventListener('pointerleave', onPointerLeave);
      document.removeEventListener('visibilitychange', onVisibility);
      themeObserver.disconnect();
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
