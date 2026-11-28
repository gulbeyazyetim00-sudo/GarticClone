import { useEffect, useRef, useState } from 'react'
import {
  DrawOp, PALETTE, isDrawOp, decodeBroadcast,
  encodeStrokeStart, encodeStrokeDeltas, encodeStrokeEnd,
} from '../net/drawProtocol.js'
import { StrokeEncoder } from '../net/strokeEncoder.js'
import { DrawRenderer } from '../net/drawRenderer.js'

const W = 800
const H = 600
const TICK_MS = 33 // ~30 Hz batching

export default function DrawingCanvas({ socket }) {
  const canvasRef = useRef(null)
  const rendererRef = useRef(null)
  const encRef = useRef(new StrokeEncoder())
  const drawingRef = useRef(false)
  const timerRef = useRef(null)
  const [color, setColor] = useState(0)
  const [thickness, setThickness] = useState(4)

  useEffect(() => {
    const r = new DrawRenderer(canvasRef.current.getContext('2d'))
    rendererRef.current = r
    const off = socket.on('message', (m) => {
      if (!isDrawOp(m.opcode)) return
      try { r.apply(decodeBroadcast(m.opcode, m.payload)) } catch (e) { console.warn('bad draw message', e) }
    })
    return () => { off(); clearInterval(timerRef.current) }
  }, [socket])

  const flush = () => {
    for (const b of encRef.current.flush()) socket.send(DrawOp.STROKE_DELTAS, encodeStrokeDeltas(b))
  }

  const pos = (ev) => {
    const c = canvasRef.current
    const r = c.getBoundingClientRect()
    return [((ev.clientX - r.left) * c.width) / r.width, ((ev.clientY - r.top) * c.height) / r.height]
  }

  const onDown = (e) => {
    if (e.button !== 0) return
    canvasRef.current.setPointerCapture(e.pointerId)
    const [x, y] = pos(e.nativeEvent)
    const start = encRef.current.begin(x, y, color, thickness)
    rendererRef.current.setStyle(start.strokeId, color, thickness)
    rendererRef.current.drawDot(start.strokeId, start.x, start.y)
    socket.send(DrawOp.STROKE_START, encodeStrokeStart(start))
    drawingRef.current = true
    timerRef.current = setInterval(flush, TICK_MS)
  }

  const onMove = (e) => {
    if (!drawingRef.current) return
    const coalesced = e.nativeEvent.getCoalescedEvents?.() ?? []
    const evs = coalesced.length ? coalesced : [e.nativeEvent]
    const enc = encRef.current
    for (const ev of evs) {
      const [x, y] = pos(ev)
      const { fromX, fromY, steps } = enc.addPoint(x, y)
      rendererRef.current.drawSteps(enc.strokeId, fromX, fromY, steps) // local echo, no waiting for the tick
    }
  }

  const onUp = () => {
    if (!drawingRef.current) return
    drawingRef.current = false
    clearInterval(timerRef.current)
    flush()
    socket.send(DrawOp.STROKE_END, encodeStrokeEnd(encRef.current.strokeId))
  }

  const clear = () => {
    rendererRef.current.apply({ type: 'clear' })
    socket.send(DrawOp.CANVAS_CLEAR)
  }

  return (
    <div>
      <div style={{ display: 'flex', gap: 6, alignItems: 'center', margin: '8px 0' }}>
        {PALETTE.map((c, i) => (
          <button key={c} onClick={() => setColor(i)}
            style={{ width: 24, height: 24, background: c, border: i === color ? '3px solid #000' : '1px solid #888' }} />
        ))}
        <input type="range" min="1" max="24" value={thickness} onChange={(e) => setThickness(+e.target.value)} />
        <button onClick={clear}>Clear</button>
      </div>
      <canvas ref={canvasRef} width={W} height={H}
        style={{ background: '#fff', border: '1px solid #888', touchAction: 'none', maxWidth: '100%' }}
        onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp} />
    </div>
  )
}
