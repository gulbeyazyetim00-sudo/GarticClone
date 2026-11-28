import { PALETTE } from './drawProtocol.js';

/** Draws decoded draw messages onto a 2D context and tracks the highest journal seq seen. */
export class DrawRenderer {
  constructor(ctx) {
    this.ctx = ctx;
    this.styles = new Map(); // strokeId -> { color, thickness }
    this.lastSeq = 0;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
  }

  setStyle(strokeId, color, thickness) { this.styles.set(strokeId, { color, thickness }); }

  #style(strokeId) {
    const s = this.styles.get(strokeId) ?? { color: 0, thickness: 4 };
    const css = PALETTE[s.color] ?? PALETTE[0];
    this.ctx.strokeStyle = css;
    this.ctx.fillStyle = css;
    this.ctx.lineWidth = s.thickness || 4;
  }

  drawDot(strokeId, x, y) {
    this.#style(strokeId);
    this.ctx.beginPath();
    this.ctx.arc(x, y, this.ctx.lineWidth / 2, 0, Math.PI * 2);
    this.ctx.fill();
  }

  drawSteps(strokeId, fromX, fromY, steps) {
    if (!steps.length) return;
    this.#style(strokeId);
    const c = this.ctx;
    let x = fromX;
    let y = fromY;
    c.beginPath();
    c.moveTo(x, y);
    for (const [dx, dy] of steps) { x += dx; y += dy; c.lineTo(x, y); }
    c.stroke();
  }

  apply(msg) {
    switch (msg.type) {
      case 'start':
        this.setStyle(msg.strokeId, msg.color, msg.thickness);
        this.drawDot(msg.strokeId, msg.x, msg.y);
        break;
      case 'deltas':
        this.drawSteps(msg.strokeId, msg.originX, msg.originY, msg.steps);
        break;
      case 'end':
        this.styles.delete(msg.strokeId);
        break;
      case 'clear':
        this.ctx.clearRect(0, 0, this.ctx.canvas.width, this.ctx.canvas.height);
        this.styles.clear();
        break;
      default:
        break; // 'ack' only updates lastSeq
    }
    if (msg.seq && msg.seq > this.lastSeq) this.lastSeq = msg.seq;
  }
}
