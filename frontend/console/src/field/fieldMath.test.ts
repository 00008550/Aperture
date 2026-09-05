import { describe, expect, it } from 'vitest';
import { buildGrid, flowAt, pulseAlive, pulseValue, shouldAnimate } from './fieldMath';

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
