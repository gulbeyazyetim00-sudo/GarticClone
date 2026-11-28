const clampI8 = (n) => Math.max(-128, Math.min(127, n));
const clampCoord = (n) => Math.max(0, Math.min(65535, Math.round(n)));
const BATCH_MAX = 255;

/**
 * Turns raw pointer positions into integer steps of at most [-128,127] per axis.
 * Large jumps are split into several steps, so batches never need an extra origin.
 */
export class StrokeEncoder {
  #nextId = Math.floor(Math.random() * 65536);

  begin(x, y, color, thickness) {
    this.strokeId = this.#nextId = (this.#nextId + 1) & 0xffff;
    this.x = clampCoord(x);
    this.y = clampCoord(y);
    this.anchorX = this.x;
    this.anchorY = this.y;
    this.steps = [];
    return { strokeId: this.strokeId, color, thickness, x: this.x, y: this.y };
  }

  /** Returns the new steps (and where they start) so the caller can draw them locally right away. */
  addPoint(x, y) {
    const tx = clampCoord(x);
    const ty = clampCoord(y);
    const fromX = this.x;
    const fromY = this.y;
    const steps = [];
    while (this.x !== tx || this.y !== ty) {
      const dx = clampI8(tx - this.x);
      const dy = clampI8(ty - this.y);
      steps.push([dx, dy]);
      this.x += dx;
      this.y += dy;
    }
    for (const s of steps) this.steps.push(s);
    return { fromX, fromY, steps };
  }

  /** Called on the 30-60 Hz tick: returns zero or more self-contained batches (each carries its origin). */
  flush() {
    const batches = [];
    let ox = this.anchorX;
    let oy = this.anchorY;
    for (let i = 0; i < this.steps.length; i += BATCH_MAX) {
      const chunk = this.steps.slice(i, i + BATCH_MAX);
      batches.push({ strokeId: this.strokeId, originX: ox, originY: oy, steps: chunk });
      for (const [dx, dy] of chunk) { ox += dx; oy += dy; }
    }
    this.anchorX = ox;
    this.anchorY = oy;
    this.steps = [];
    return batches;
  }
}
