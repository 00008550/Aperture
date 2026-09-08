import { describe, expect, it } from 'vitest';
import {
  buildGrid,
  buildPaneGrid,
  flowAt,
  idleShimmer,
  pulseAlive,
  pulseValue,
  refractAt,
  seamOf,
  shouldAnimate,
} from './fieldMath';

describe('buildGrid — grid geometry for a viewport', () => {
  it('fills a viewport with blocks that stay inside its bounds', () => {
    const grid = buildGrid(800, 600, { cell: 34, gap: 2 });
    expect(grid.cols).toBeGreaterThan(0);
    expect(grid.rows).toBeGreaterThan(0);
    expect(grid.blocks).toHaveLength(grid.cols * grid.rows);
    for (const block of grid.blocks) {
      expect(block.x).toBeGreaterThanOrEqual(0);
      expect(block.y).toBeGreaterThanOrEqual(0);
      expect(block.x).toBeLessThanOrEqual(800);
      expect(block.y).toBeLessThanOrEqual(600);
    }
  });

  it('grows the cell so the block count never exceeds the cap (60fps guard)', () => {
    const grid = buildGrid(4000, 3000, { cell: 10, gap: 0, maxBlocks: 500 });
    expect(grid.blocks.length).toBeLessThanOrEqual(500);
    expect(grid.cell).toBeGreaterThan(10); // it had to grow to fit under the cap
  });

  it('yields no blocks for a zero-area viewport', () => {
    expect(buildGrid(0, 600).blocks).toHaveLength(0);
    expect(buildGrid(800, 0).blocks).toHaveLength(0);
    expect(buildGrid(-5, -5).blocks).toHaveLength(0);
  });
});

describe('buildPaneGrid — Aurora Glass pane grid + seam spacing (010-P2)', () => {
  it('lays out big rounded panes separated by a uniform thin seam', () => {
    const grid = buildPaneGrid(800, 600, { pane: 46, seam: 3 });
    expect(grid.cols).toBeGreaterThan(1);
    expect(grid.rows).toBeGreaterThan(1);
    // The pane edge is at least the requested size.
    expect(grid.cell).toBeGreaterThanOrEqual(46);
    // Adjacent panes on a row are exactly one stride (pane edge + seam) apart.
    const first = grid.blocks[0]!;
    const second = grid.blocks[1]!;
    const stride = second.x - first.x;
    expect(stride).toBeCloseTo(grid.cell + 3, 6);
    // So the seam between panes is exactly the requested 3px.
    expect(stride - grid.cell).toBeCloseTo(3, 6);
    expect(seamOf(grid, 3)).toBe(3);
  });

  it('grows the pane so the count never exceeds the cap (60fps guard)', () => {
    const grid = buildPaneGrid(4000, 3000, { pane: 20, seam: 2, maxPanes: 300 });
    expect(grid.blocks.length).toBeLessThanOrEqual(300);
    expect(grid.cell).toBeGreaterThan(20);
  });
});

describe('refractAt — glass panes lean/bulge toward the cursor (010-P2)', () => {
  it('is inert with no pointer or beyond the influence radius', () => {
    expect(refractAt(100, 100, null)).toEqual({ intensity: 0, dx: 0, dy: 0 });
    expect(refractAt(0, 0, { x: 500, y: 500 }, { radius: 150 })).toEqual({ intensity: 0, dx: 0, dy: 0 });
  });

  it('intensifies as the pointer nears and leans the pane *toward* the pointer', () => {
    const near = refractAt(100, 100, { x: 90, y: 100 }, { radius: 190, lean: 5 });
    const far = refractAt(100, 100, { x: 10, y: 100 }, { radius: 190, lean: 5 });
    expect(near.intensity).toBeGreaterThan(far.intensity);
    expect(near.intensity).toBeLessThanOrEqual(1);
    // Pointer is to the LEFT of the pane, so the pane leans left (toward it): dx < 0. This is the
    // opposite sign to the P1 dot `flowAt`, which pushes away.
    expect(near.dx).toBeLessThan(0);
    expect(Math.abs(near.dy)).toBeLessThan(1e-9);
  });

  it('bulges without leaning when the pointer is exactly on the pane', () => {
    const r = refractAt(100, 100, { x: 100, y: 100 }, { radius: 190 });
    expect(r.intensity).toBeGreaterThan(0);
    expect(r.dx).toBe(0);
    expect(r.dy).toBe(0);
  });

  it('keeps the lean low-amplitude (quiet-but-alive)', () => {
    const r = refractAt(100, 100, { x: 95, y: 100 }, { radius: 190, lean: 5 });
    expect(Math.hypot(r.dx, r.dy)).toBeLessThanOrEqual(5);
  });
});

describe('idleShimmer — quiet idle life far from the pointer (010-P2)', () => {
  it('stays within [0, amplitude] for any pane and time', () => {
    for (const now of [0, 250, 999, 5000, 123456]) {
      for (const col of [0, 3, 17]) {
        const v = idleShimmer(col, col + 2, now, 0.06);
        expect(v).toBeGreaterThanOrEqual(0);
        expect(v).toBeLessThanOrEqual(0.06);
      }
    }
  });
});

describe('flowAt — pointer-proximity flow function', () => {
  it('is inert with no pointer', () => {
    expect(flowAt(100, 100, null)).toEqual({ intensity: 0, dx: 0, dy: 0 });
  });

  it('is inert beyond the influence radius', () => {
    const flow = flowAt(0, 0, { x: 500, y: 500 }, { radius: 150 });
    expect(flow).toEqual({ intensity: 0, dx: 0, dy: 0 });
  });

  it('intensifies as the pointer nears and pushes the block away from it', () => {
    const near = flowAt(100, 100, { x: 90, y: 100 }, { radius: 150, amplitude: 7 });
    const far = flowAt(100, 100, { x: 10, y: 100 }, { radius: 150, amplitude: 7 });
    expect(near.intensity).toBeGreaterThan(far.intensity);
    expect(near.intensity).toBeLessThanOrEqual(1);
    // block is to the right of the pointer, so displacement pushes it further right (+x)
    expect(near.dx).toBeGreaterThan(0);
    expect(Math.abs(near.dy)).toBeLessThan(1e-9);
  });

  it('lifts without displacing when the pointer is exactly on the block', () => {
    const flow = flowAt(100, 100, { x: 100, y: 100 }, { radius: 150 });
    expect(flow.intensity).toBeGreaterThan(0);
    expect(flow.dx).toBe(0);
    expect(flow.dy).toBe(0);
  });

  it('keeps displacement low-amplitude (quiet-but-alive)', () => {
    const flow = flowAt(100, 100, { x: 95, y: 100 }, { radius: 150, amplitude: 7 });
    expect(Math.hypot(flow.dx, flow.dy)).toBeLessThanOrEqual(7);
  });
});

describe('pulseValue / pulseAlive — click pulse ripples out and decays (edge 3)', () => {
  it('contributes nothing before emission or after its lifetime', () => {
    expect(pulseValue(-1, 0, { duration: 1100 })).toBe(0);
    expect(pulseValue(1100, 0, { duration: 1100 })).toBe(0);
    expect(pulseValue(5000, 0, { duration: 1100 })).toBe(0);
  });

  it('lights a ring that expands outward over time', () => {
    // At a later time the lit distance moves further from the origin.
    const early = pulseValue(100, 55, { speed: 0.55, width: 110, duration: 1100 });
    const later = pulseValue(400, 220, { speed: 0.55, width: 110, duration: 1100 });
    expect(early).toBeGreaterThan(0);
    expect(later).toBeGreaterThan(0);
  });

  it('decays to zero within the bounded lifetime for every block', () => {
    const distances = [0, 50, 120, 300, 600, 1200];
    const duration = 1100;
    for (const d of distances) {
      // Sample densely across the lifetime; the value must never exceed 1 and must be 0 at the end.
      for (let t = 0; t <= duration; t += 25) {
        const v = pulseValue(t, d, { duration });
        expect(v).toBeGreaterThanOrEqual(0);
        expect(v).toBeLessThanOrEqual(1);
      }
      expect(pulseValue(duration, d, { duration })).toBe(0);
    }
  });

  it('reports a pulse alive only within [start, start+duration)', () => {
    expect(pulseAlive(1000, 1000, 1100)).toBe(true);
    expect(pulseAlive(1000, 2000, 1100)).toBe(true);
    expect(pulseAlive(1000, 2100, 1100)).toBe(false); // exactly at the end
    expect(pulseAlive(1000, 900, 1100)).toBe(false); // before emission
  });
});

describe('shouldAnimate — animation gate (edge 1, edge 4)', () => {
  it('runs the loop only when visible and motion is allowed', () => {
    expect(shouldAnimate({ hidden: false, reducedMotion: false })).toBe(true);
  });

  it('pauses when the tab is hidden', () => {
    expect(shouldAnimate({ hidden: true, reducedMotion: false })).toBe(false);
  });

  it('never runs the loop under reduced motion', () => {
    expect(shouldAnimate({ hidden: false, reducedMotion: true })).toBe(false);
    expect(shouldAnimate({ hidden: true, reducedMotion: true })).toBe(false);
  });
});
