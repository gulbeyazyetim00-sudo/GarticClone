using System;
using System.Collections.Generic;
using System.Text;

namespace GarticClone.Server.Protocol.Opcodes;

public static class SessionOp
{
    // SystemOp 0x00-0x0F, Çizim paketleri 0x10-0x1F kullanacağı için
    // çakışma olmaması adına 0x20-0x2F aralığını ayırdık.
    public static readonly OpcodeRange Range = new("session", 0x20, 0x2F);

    public const byte RoomCreate = 0x20; // Client -> Server: Yeni oda aç
    public const byte RoomJoin = 0x21; // Client -> Server: Odaya katıl
    public const byte RoomLeave = 0x22; // Client -> Server: Odadan çık
    public const byte RoomState = 0x23; // Server -> Client: Odanın güncel durumu
    public const byte GameStart = 0x24; // Client -> Server: Oyunu başlat (sadece Host)
    public const byte PhaseSubmit = 0x25; // Client -> Server: Metin/çizim/tahmin gönder
    public const byte PhaseChanged = 0x26; // Server -> Client: Tur değişti bildirimi
    public const byte SyncRequest = 0x27; // Client -> Server: Reconnect / geç katılım senkronizasyonu iste
    public const byte SyncSnapshot = 0x28; // Server -> Client: Keyframe + eksik delta paketleri
    public const byte PlayerStatus = 0x29; // Server -> Client: Oyuncu bağlandı / koptu / atıldı
}