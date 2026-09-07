// Pure geometry + motion math for the reactive block-field. No DOM, no canvas, no React here —
// everything in this file is a deterministic function so the field's behaviour (grid layout,
// pointer flow, click-pulse decay, animation gating) is unit-testable in jsdom without a real
// 2D context. BlockField.tsx wires these into a single requestAnimationFrame loop.

export interface Block {
  /** column / row index in the grid */
  readonly col: number;
  readonly row: number;
  /** centre of the block in CSS pixels */
  readonly x: number;
  readonly y: number;
  /** edge length of the block in CSS pixels */
  readonly size: number;
}

export interface Grid {
  readonly cols: number;
  readonly rows: number;
  readonly cell: number;
  readonly blocks: readonly Block[];
}

export interface GridOptions {
  /** desired edge length of a block, in CSS pixels (grown to honour maxBlocks) */
  cell?: number;
  /** gap between block cells, in CSS pixels */
  gap?: number;
  /** hard cap on block count — the field grows the cell to stay under it (60fps guard) */
  maxBlocks?: number;
}

const DEFAULT_GRID: Required<GridOptions> = { cell: 34, gap: 2, maxBlocks: 900 };

/**
 * Lay out a grid of blocks that fills a `width`×`height` viewport. The cell size is grown until the
 * block count is at or below `maxBlocks`, so the field never renders more than it can paint at
 * 60fps regardless of how large the window is. An empty (zero-area) viewport yields no blocks.
 */
export function buildGrid(width: number, height: number, options: GridOptions = {}): Grid {
  const { cell, gap, maxBlocks } = { ...DEFAULT_GRID, ...options };
  const w = Math.max(0, width);
  const h = Math.max(0, height);
  if (w === 0 || h === 0) {
    return { cols: 0, rows: 0, cell, blocks: [] };
  }

  let size = Math.max(8, cell);
  let cols = Math.max(1, Math.floor(w / (size + gap)));
  let rows = Math.max(1, Math.floor(h / (size + gap)));

  // Grow the cell until the total block count fits under the cap. Bounded: each step strictly
  // increases `size`, which monotonically decreases cols*rows toward 1.
  while (cols * rows > maxBlocks) {
    size += 2;
    cols = Math.max(1, Math.floor(w / (size + gap)));
    rows = Math.max(1, Math.floor(h / (size + gap)));
  }

  const stride = size + gap;
  // Centre the grid so leftover space is split evenly on both edges.
  const offsetX = (w - cols * stride + gap) / 2;
  const offsetY = (h - rows * stride + gap) / 2;

  const blocks: Block[] = [];
  for (let row = 0; row < rows; row++) {
    for (let col = 0; col < cols; col++) {
      blocks.push({
        col,
        row,
        x: offsetX + col * stride + size / 2,
        y: offsetY + row * stride + size / 2,
        size,
      });
    }
  }

  return { cols, rows, cell: size, blocks };
}

export interface Pointer {
  readonly x: number;
  readonly y: number;
}

export interface Flow {
  /** 0..1 proximity intensity — drives brightness/scale of the block */
  readonly intensity: number;
  /** displacement in CSS pixels — the block flows away from the pointer */
  readonly dx: number;
  readonly dy: number;
}

export interface FlowOptions {
  /** pointer influence radius in CSS pixels */
  radius?: number;
  /** peak displacement in CSS pixels at the pointer (kept low — quiet-but-alive) */
  amplitude?: number;
}

const NO_FLOW: Flow = { intensity: 0, dx: 0, dy: 0 };
const DEFAULT_FLOW: Required<FlowOptions> = { radius: 150, amplitude: 7 };

/**
 * Pointer-proximity flow for one block. Within `radius` the block gains a smooth (quadratic
 * falloff) intensity and is displaced *away* from the pointer — so blocks flow outward in front of
 * the cursor and settle back as it passes. Outside the radius, or with no pointer, it is inert.
 */
export function flowAt(bx: number, by: number, pointer: Pointer | null, options: FlowOptions = {}): Flow {
  if (!pointer) return NO_FLOW;
  const { radius, amplitude } = { ...DEFAULT_FLOW, ...options };
  const dx = bx - pointer.x;
  const dy = by - pointer.y;
  const dist = Math.hypot(dx, dy);
  if (dist >= radius) return NO_FLOW;

  const t = 1 - dist / radius; // 1 at the pointer, 0 at the radius edge
  const falloff = t * t; // quadratic — gentle shoulders, no hard ring
  if (dist === 0) {
    // Exactly on the pointer there is no direction; lift without displacing.
    return { intensity: falloff, dx: 0, dy: 0 };
  }
  const ux = dx / dist;
  const uy = dy / dist;
  const push = falloff * amplitude;
  return { intensity: falloff, dx: ux * push, dy: uy * push };
}

export interface Pulse {
  readonly x: number;
  readonly y: number;
  /** timestamp (ms) the pulse was emitted — compare against the current frame time */
  readonly start: number;
}

export interface PulseOptions {
  /** expansion speed of the ring, CSS pixels per ms */
  speed?: number;
  /** thickness of the ring, CSS pixels — blocks within it are lit */
  width?: number;
  /** total lifetime, ms — the pulse is guaranteed zero at and after this */
  duration?: number;
}

const DEFAULT_PULSE: Required<PulseOptions> = { speed: 0.55, width: 110, duration: 1100 };

/**
 * Intensity (0..1) a click-pulse contributes to a block `distance` px from the pulse origin,
 * `elapsed` ms after it was emitted. The lit ring expands at `speed` and the whole pulse decays
 * linearly to exactly zero at `duration` — so it always ripples outward and always dies in bounded
 * time (edge 3). Before emission or after its lifetime it contributes nothing.
 */
export function pulseValue(elapsed: number, distance: number, options: PulseOptions = {}): number {
  if (elapsed < 0 || elapsed >= (options.duration ?? DEFAULT_PULSE.duration)) return 0;
  const { speed, width, duration } = { ...DEFAULT_PULSE, ...options };
  const ringRadius = elapsed * speed;
  const offset = Math.abs(distance - ringRadius);
  if (offset > width) return 0;
  const ring = 1 - offset / width; // brightest at the ring crest
  const decay = 1 - elapsed / duration; // linear fade, hits 0 at duration
  return ring * decay;
}

/** Whether a pulse emitted at `start` is still contributing at frame time `now`. */
export function pulseAlive(start: number, now: number, duration: number = DEFAULT_PULSE.duration): boolean {
  const elapsed = now - start;
  return elapsed >= 0 && elapsed < duration;
}

export interface AnimationGate {
  /** the tab/document is hidden (document.hidden / visibilitychange) */
  hidden: boolean;
  /** prefers-reduced-motion: reduce is in effect */
  reducedMotion: boolean;
}

/**
 * Whether the rAF loop should run. Fails closed toward stillness: a hidden tab or a
 * reduced-motion preference pauses the loop entirely (edge 1, edge 4). BlockField consults this
 * before scheduling every frame.
 */
export function shouldAnimate({ hidden, reducedMotion }: AnimationGate): boolean {
  return !hidden && !reducedMotion;
}
