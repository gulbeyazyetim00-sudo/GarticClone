using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace GarticClone.Server.Transport;

public enum ConnectionState { Open, Closing, Closed }

public enum TrafficDirection { In, Out }


public readonly record struct TrafficEvent(TrafficDirection Direction, int Bytes, byte WsOpcode);

public readonly record struct WsMessage(bool IsBinary, byte[] Data);


public sealed class WebSocketConnection
{
    private static int _nextId;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly FrameParser _parser;
    private readonly int _maxMessageSize;
    private readonly TimeSpan _closeTimeout;
    private readonly Channel<OutFrame> _outbound = Channel.CreateUnbounded<OutFrame>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _stateLock = new();

    private Fragments? _fragments;
    private (ushort Code, string Reason)? _closeInfo;
    private Timer? _closeTimer;
    private long _bufferedBytes;
    private int _finalized;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public string RemoteAddress { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Open;

    public long BufferedAmount => Interlocked.Read(ref _bufferedBytes);

    public event Action<WebSocketConnection, WsMessage>? MessageReceived;
    public event Action<byte[]>? PingReceived;
    public event Action<byte[]>? PongReceived;
    public event Action<TrafficEvent>? Traffic;
    public event Action<ushort, string>? Closed;

    public WebSocketConnection(Socket socket, NetworkStream stream, int maxMessageSize = 1 << 20, TimeSpan? closeTimeout = null)
    {
        _socket = socket;
        _stream = stream;
        _maxMessageSize = maxMessageSize;
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(5);
        _parser = new FrameParser(maxMessageSize, requireMask: true);
        RemoteAddress = socket.RemoteEndPoint?.ToString() ?? "unknown";

        _socket.NoDelay = true;
    }

   
    public void Start(byte[] leftover)
    {
        _ = Task.Run(WriteLoopAsync);
        if (leftover.Length > 0) Ingest(leftover);
        _ = Task.Run(ReadLoopAsync);
    }

    public bool Send(ReadOnlySpan<byte> data, bool binary = true)
    {
        lock (_stateLock)
        {
            if (State != ConnectionState.Open) return false;
            Enqueue(binary ? WsOpcode.Binary : WsOpcode.Text, data);
            return true;
        }
    }

    public bool SendText(string text) => Send(Encoding.UTF8.GetBytes(text), binary: false);

    public bool Ping(ReadOnlySpan<byte> payload)
    {
        lock (_stateLock)
        {
            if (State != ConnectionState.Open) return false;
            Enqueue(WsOpcode.Ping, payload);
            return true;
        }
    }

    public void Close(ushort code = CloseCode.Normal, string reason = "")
    {
        lock (_stateLock)
        {
            if (State != ConnectionState.Open) return;
            State = ConnectionState.Closing;
            _closeInfo = (code, reason);
            Enqueue(WsOpcode.Close, FrameEncoder.ClosePayload(code, reason));
        }
        ArmCloseTimer();
    }

    public void Terminate()
    {
        try { _socket.Close(0); } catch { }
    }

    internal void Ingest(ReadOnlySpan<byte> chunk)
    {
        if (State == ConnectionState.Closed) return;

        List<Frame> frames;
        try
        {
            frames = _parser.Push(chunk);
        }
        catch (WsProtocolException ex)
        {
            Fail(ex.CloseCode, ex.Message);
            return;
        }

        foreach (var frame in frames)
        {
            Traffic?.Invoke(new TrafficEvent(TrafficDirection.In, frame.TotalBytes, frame.Opcode));
            HandleFrame(frame);
            if (State == ConnectionState.Closed) return;
        }
    }


    private void HandleFrame(Frame frame)
    {
        switch (frame.Opcode)
        {
            case WsOpcode.Ping:
                lock (_stateLock)
                {
                    if (State == ConnectionState.Open) Enqueue(WsOpcode.Pong, frame.Payload);
                }
                PingReceived?.Invoke(frame.Payload);
                break;

            case WsOpcode.Pong:
                PongReceived?.Invoke(frame.Payload);
                break;

            case WsOpcode.Close:
                HandleCloseFrame(frame.Payload);
                break;

            case WsOpcode.Text:
            case WsOpcode.Binary:
                if (_fragments is not null)
                {
                    Fail(CloseCode.ProtocolError, "new data frame while fragmented message pending");
                    return;
                }
                if (frame.Fin)
                {
                    Deliver(frame.Opcode, frame.Payload);
                }
                else
                {
                    _fragments = new Fragments(frame.Opcode);
                    _fragments.Add(frame.Payload);
                }
                break;

            case WsOpcode.Continuation:
                if (_fragments is null)
                {
                    Fail(CloseCode.ProtocolError, "continuation without start frame");
                    return;
                }
                if (_fragments.Size + frame.Payload.Length > _maxMessageSize)
                {
                    Fail(CloseCode.MessageTooBig, "reassembled message too big");
                    return;
                }
                _fragments.Add(frame.Payload);
                if (frame.Fin)
                {
                    var f = _fragments;
                    _fragments = null;
                    Deliver(f.Opcode, f.Concat());
                }
                break;
        }
    }

    private void Deliver(byte opcode, byte[] data)
    {
        if (State != ConnectionState.Open) return; 
        if (opcode == WsOpcode.Text && !IsValidUtf8(data))
        {
            Fail(CloseCode.InvalidPayload, "invalid UTF-8 in text message");
            return;
        }
        MessageReceived?.Invoke(this, new WsMessage(opcode == WsOpcode.Binary, data));
    }

    private void HandleCloseFrame(byte[] payload)
    {
        ushort code = CloseCode.NoStatus;
        string reason = "";

        if (payload.Length == 1)
        {
            Fail(CloseCode.ProtocolError, "close payload of length 1");
            return;
        }
        if (payload.Length >= 2)
        {
            code = BinaryPrimitives.ReadUInt16BigEndian(payload);
            if (!CloseCode.IsValidOnWire(code))
            {
                Fail(CloseCode.ProtocolError, $"invalid close code {code}");
                return;
            }
            var reasonBytes = payload.AsSpan(2);
            if (!IsValidUtf8(reasonBytes))
            {
                Fail(CloseCode.InvalidPayload, "invalid UTF-8 in close reason");
                return;
            }
            reason = Encoding.UTF8.GetString(reasonBytes);
        }

        lock (_stateLock)
        {
            if (State == ConnectionState.Open)
            {
                
                State = ConnectionState.Closing;
                _closeInfo = (code, reason);
                byte[] echo = code == CloseCode.NoStatus ? [] : FrameEncoder.ClosePayload(code);
                Enqueue(WsOpcode.Close, echo, closeTcpAfter: true);
            }
            else
            {
                EnqueueTcpClose();
            }
        }
        ArmCloseTimer();
    }

    private void Fail(ushort code, string reason)
    {
        lock (_stateLock)
        {
            if (State == ConnectionState.Closed) return;
            if (State == ConnectionState.Open)
            {
                State = ConnectionState.Closing;
                _closeInfo = (code, reason);
                Enqueue(WsOpcode.Close, FrameEncoder.ClosePayload(code, reason), closeTcpAfter: true);
            }
            else
            {
                EnqueueTcpClose();
            }
        }
        ArmCloseTimer();
    }


    private async Task ReadLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n = await _stream.ReadAsync(buffer);
                if (n == 0) break; 
                Ingest(buffer.AsSpan(0, n));
                if (State == ConnectionState.Closed) break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            
        }
        finally
        {
            FinalizeClose();
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync())
            {
                if (item.Bytes.Length > 0)
                {
                    await _stream.WriteAsync(item.Bytes);
                    Interlocked.Add(ref _bufferedBytes, -item.Bytes.Length);
                    Traffic?.Invoke(new TrafficEvent(TrafficDirection.Out, item.Bytes.Length, item.Opcode));
                }
                if (item.CloseTcpAfter)
                {
                    try { _socket.Shutdown(SocketShutdown.Send); } catch { }
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Terminate();
        }
    }

    private void Enqueue(byte opcode, ReadOnlySpan<byte> payload, bool closeTcpAfter = false)
    {
        byte[] frame = FrameEncoder.Encode(opcode, payload);
        Interlocked.Add(ref _bufferedBytes, frame.Length);
        _outbound.Writer.TryWrite(new OutFrame(frame, opcode, closeTcpAfter));
    }

    private void EnqueueTcpClose() =>
        _outbound.Writer.TryWrite(new OutFrame([], 0, CloseTcpAfter: true));

    private void ArmCloseTimer()
    {
        
        if (Volatile.Read(ref _closeTimer) is not null) return;
        var timer = new Timer(_ => Terminate(), null, Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _closeTimer, timer, null) is null)
            timer.Change(_closeTimeout, Timeout.InfiniteTimeSpan);
        else
            timer.Dispose();
    }

    private void FinalizeClose()
    {
        if (Interlocked.Exchange(ref _finalized, 1) == 1) return;

        (ushort Code, string Reason) info;
        lock (_stateLock)
        {
            State = ConnectionState.Closed;
            info = _closeInfo ?? (CloseCode.Abnormal, "");
        }
        _closeTimer?.Dispose();
        _outbound.Writer.TryComplete();
        try { _socket.Close(); } catch {  }

        Closed?.Invoke(info.Code, info.Reason);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static bool IsValidUtf8(ReadOnlySpan<byte> data)
    {
        try
        {
            StrictUtf8.GetCharCount(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private readonly record struct OutFrame(byte[] Bytes, byte Opcode, bool CloseTcpAfter);

    private sealed class Fragments(byte opcode)
    {
        private readonly List<byte[]> _chunks = [];
        public byte Opcode { get; } = opcode;
        public int Size { get; private set; }

        public void Add(byte[] chunk)
        {
            _chunks.Add(chunk);
            Size += chunk.Length;
        }

        public byte[] Concat()
        {
            var result = new byte[Size];
            int offset = 0;
            foreach (var c in _chunks)
            {
                c.CopyTo(result, offset);
                offset += c.Length;
            }
            return result;
        }
    }
}
