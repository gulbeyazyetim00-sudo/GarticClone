using System;
using System.Collections.Generic;

namespace GarticClone.Server.Modules.Session;

public sealed record DeltaEntry(uint Seq, byte[] Payload);

public sealed record CatchUpResult(
    bool FullSnapshotIncluded,
    string? KeyframeSnapshotBase64,
    uint SnapshotSeq,
    uint LatestSeq,
    IReadOnlyList<DeltaItemDto> MissedDeltas);

/// <summary>
/// Sabit kapasiteli dairesel tampon (Ring Buffer).
/// Son K adet çizim delta paketini RAM'de O(1) karmaşıklıkla tutar.
/// </summary>
public sealed class DeltaRingBuffer
{
    private readonly object _lock = new();
    private readonly DeltaEntry?[] _buffer;
    private readonly int _capacity;

    private int _head;        // Bir sonraki yazma indeksi
    private int _count;       // Tampondaki aktif eleman sayısı
    private uint _currentSeq; // Artan sıra numarası

    private string? _keyframeSnapshotBase64;
    private uint _snapshotSeq;

    public DeltaRingBuffer(int capacity = 500)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer = new DeltaEntry?[capacity];
    }

    public uint CurrentSeq
    {
        get { lock (_lock) return _currentSeq; }
    }

    /// <summary>
    /// Yeni bir çizim delta paketini O(1) sürede ring buffer'a ekler.
    /// Kapasite dolunca en eski paketin üzerine yazar.
    /// </summary>
    public uint Append(byte[] payload)
    {
        lock (_lock)
        {
            _currentSeq++;
            _buffer[_head] = new DeltaEntry(_currentSeq, payload);
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
            return _currentSeq;
        }
    }

    /// <summary>
    /// Tuvalin periyodik tam görüntüsünü (Keyframe Snapshot) kaydeder.
    /// </summary>
    public void SaveKeyframe(string snapshotBase64)
    {
        lock (_lock)
        {
            _keyframeSnapshotBase64 = snapshotBase64;
            _snapshotSeq = _currentSeq;
        }
    }

    /// <summary>
    /// Kopup geri gelen veya geç katılan oyuncuya sadece eksik paketleri hazırlar.
    /// </summary>
    public CatchUpResult GetCatchUp(uint lastAckSeq)
    {
        lock (_lock)
        {
            uint oldestSeq = GetOldestSeqUnsafe();

            // İstemci hiç veri almadıysa (0) veya tamponun kapsayamayacağı kadar geride kaldıysa:
            // Önce Keyframe Snapshot ver, üstüne snapshot sonrası deltaları ekle.
            bool needsFullSnapshot = lastAckSeq == 0 || (_count > 0 && lastAckSeq < oldestSeq - 1);

            uint replayFromSeq = needsFullSnapshot ? _snapshotSeq : lastAckSeq;
            var missed = new List<DeltaItemDto>();

            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                var entry = _buffer[(start + i) % _capacity];
                if (entry is not null && entry.Seq > replayFromSeq)
                {
                    missed.Add(new DeltaItemDto(entry.Seq, Convert.ToBase64String(entry.Payload)));
                }
            }

            return new CatchUpResult(
                FullSnapshotIncluded: needsFullSnapshot,
                KeyframeSnapshotBase64: needsFullSnapshot ? _keyframeSnapshotBase64 : null,
                SnapshotSeq: _snapshotSeq,
                LatestSeq: _currentSeq,
                MissedDeltas: missed);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
            _keyframeSnapshotBase64 = null;
            _snapshotSeq = _currentSeq;
        }
    }

    private uint GetOldestSeqUnsafe()
    {
        if (_count == 0) return _currentSeq;
        int oldestIndex = (_head - _count + _capacity) % _capacity;
        return _buffer[oldestIndex]?.Seq ?? _currentSeq;
    }
}