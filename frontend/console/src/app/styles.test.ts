import { describe, expect, it } from 'vitest';
// Read as text through Vite's raw loader; a plain CSS import is emptied under Vitest (`css: false`).
const css = Object.values(
  import.meta.glob<string>('../styles.css', { query: '?raw', import: 'default', eager: true }),
)[0]!;

/**
 * jsdom does not lay out, so a stylesheet collision is invisible to every render test. This one
 * was real: the block-field canvas rule was a bare `.field { position: fixed; inset: 0;
 * pointer-events: none }`, and every form row (`<label class="field">`) matched it — the forms
 * rendered as one pile of labels pinned to the panel's corner and ignored the pointer. The rule
 * must stay scoped to the canvas element.
 */
describe('styles.css', () => {
  it('scopes the fixed, pointer-less block-field rule to the canvas, never to the bare .field form-row class', () => {
    const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(([, selector, body]) => ({
      selectors: selector!
        .replace(/\/\*[\s\S]*?\*\//g, '')
        .split(',')
        .map((s) => s.trim()),
      body: body!,
    }));

    const fixedField = rules.filter(
      (rule) => /position:\s*fixed/.test(rule.body) && rule.selectors.some((s) => /\.field\b(?!-)/.test(s)),
    );
    expect(fixedField.length).toBeGreaterThan(0);
    for (const rule of fixedField) {
      for (const selector of rule.selectors.filter((s) => /\.field\b(?!-)/.test(s))) {
        expect(selector).toBe('canvas.field');
      }
    }
  });
});
