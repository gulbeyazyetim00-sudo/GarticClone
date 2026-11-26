using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Tests.Transport;

public class HandshakeTests
{
    private static HttpRequest Request(string method = "GET", Action<Dictionary<string, string>>? edit = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Upgrade"] = "websocket",
            ["Connection"] = "keep-alive, Upgrade",
            ["Sec-WebSocket-Version"] = "13",
            ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==",
            ["Origin"] = "http://localhost:5173",
        };
        edit?.Invoke(headers);
        return new HttpRequest { Method = method, Target = "/ws", Path = "/ws", Headers = headers };
    }

    [Test("accept key matches RFC 6455 §1.3 example")]
    public void AcceptKey() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", Handshake.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));

    [Test("valid request accepted (multi-token Connection header)")]
    public void ValidRequest() => Assert.True(Handshake.Validate(Request()).Ok);

    [Test("wrong version -> 426 with Sec-WebSocket-Version")]
    public void WrongVersion()
    {
        var r = Handshake.Validate(Request(edit: h => h["Sec-WebSocket-Version"] = "8"));
        Assert.Equal(426, r.Status);
        Assert.Equal("13", r.ExtraHeaders!["Sec-WebSocket-Version"]);
    }

    [Test("bad key length -> 400")]
    public void BadKey() =>
        Assert.Equal(400, Handshake.Validate(Request(edit: h => h["Sec-WebSocket-Key"] = "c2hvcnQ=")).Status);

    [Test("non-GET -> 405, missing Upgrade -> 400")]
    public void MethodAndUpgrade()
    {
        Assert.Equal(405, Handshake.Validate(Request("POST")).Status);
        Assert.Equal(400, Handshake.Validate(Request(edit: h => h.Remove("Upgrade"))).Status);
    }

    [Test("origin allow-list")]
    public void Origins()
    {
        Assert.Equal(403, Handshake.Validate(Request(), ["http://evil.com"]).Status);
        Assert.True(Handshake.Validate(Request(), ["http://localhost:5173"]).Ok);
    }

    [Test("HTTP head parser: case-insensitive headers, path without query")]
    public void ParseHead()
    {
        var req = HttpRequest.Parse("GET /ws?room=1 HTTP/1.1\r\nhost: x\r\nUPGRADE: websocket");
        Assert.NotNull(req);
        Assert.Equal("/ws", req!.Path);
        Assert.Equal("websocket", req.Header("Upgrade"));
        Assert.True(HttpRequest.Parse("garbage") is null);
    }
}
