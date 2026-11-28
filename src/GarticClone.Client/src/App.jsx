import { useEffect, useState } from 'react'
import { GameSocket } from './net/GameSocket.js'
import { SessionOp, sessionOpName } from './net/sessionOps.js'
import { decodeJson } from '@shared/protocol.js'
import DrawingCanvas from './components/DrawingCanvas.jsx'

export default function App() {
  const [sock] = useState(() => new GameSocket({ clientName: 'dev-client' }))
  const [status, setStatus] = useState('connecting')
  const [rtt, setRtt] = useState(null)
  const [log, setLog] = useState([])
  const [room, setRoom] = useState(null)
  const [roomId, setRoomId] = useState('')
  const [name, setName] = useState('player')

  const addLog = (line) => setLog((l) => [line, ...l].slice(0, 30))

  useEffect(() => {
    sock.on('open', () => setStatus('open'))
    sock.on('welcome', (w) => { setStatus('ready'); addLog('WELCOME ' + JSON.stringify(w)) })
    sock.on('close', (e) => setStatus('closed ' + e.code))
    sock.on('rtt', (r) => setRtt(r.srtt))
    sock.on('error', (e) => addLog('ERROR ' + JSON.stringify(e)))
    sock.on('message', (m) => {
      const label = sessionOpName(m.opcode)
      if (!label) return // draw messages are handled by DrawingCanvas
      let body = ''
      try { body = JSON.stringify(decodeJson(m.payload)) } catch { body = `${m.payload.length} bytes` }
      addLog(`${label} ${body}`)
      if (m.opcode === SessionOp.ROOM_STATE) setRoom(decodeJson(m.payload))
    })
    sock.connect()
    return () => sock.close()
  }, [sock])

  return (
    <div style={{ padding: 16, fontFamily: 'monospace' }}>
      <p>status: {status} | srtt: {rtt ? rtt.toFixed(1) + ' ms' : '-'} {room && `| room ${room.roomId} (you: ${room.yourPlayerId})`}</p>
      <input value={name} onChange={(e) => setName(e.target.value)} placeholder="name" />
      <button onClick={() => sock.createRoom(name)}>Create room</button>
      <input value={roomId} onChange={(e) => setRoomId(e.target.value)} placeholder="roomId" />
      <button onClick={() => sock.joinRoom(roomId, name)}>Join</button>
      <button onClick={() => sock.requestTelemetry()}>Telemetry</button>
      {room && <DrawingCanvas socket={sock} />}
      <pre style={{ maxHeight: 160, overflow: 'auto' }}>{log.join('\n')}</pre>
    </div>
  )
}
