using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StellarDotnetSdk.Requests;
using StellarDotnetSdk.Responses;
using StellarDotnetSdk.Soroban;

namespace StellarDotnetSdk.Tests.Requests;

/// <summary>
///     Verifies that the SDK's default HTTP clients advertise gzip/deflate and transparently decode compressed
///     responses, against a real loopback socket so the actual handler pipeline is exercised.
/// </summary>
[TestClass]
public class ResponseCompressionTests
{
    private const string HealthJson = "{\"database_connected\":true,\"core_up\":true,\"core_synced\":true}";

    private const string RpcHealthJson =
        "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"status\":\"healthy\",\"latestLedger\":42}}";

    [TestMethod]
    public async Task DefaultStellarSdkHttpClient_AdvertisesGzipAndDecodesResponse()
    {
        using var server = new CompressingServer(HealthJson);
        using var client = new DefaultStellarSdkHttpClient();

        var body = await client.GetStringAsync(server.Url);

        Assert.AreEqual(HealthJson, body);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
    }

    [TestMethod]
    public async Task DefaultStellarSdkHttpClient_WithResilienceOptions_AdvertisesGzipAndDecodesResponse()
    {
        using var server = new CompressingServer(HealthJson);
        using var client = new DefaultStellarSdkHttpClient(
            resilienceOptions: new HttpResilienceOptions { MaxRetryCount = 1 });

        var body = await client.GetStringAsync(server.Url);

        Assert.AreEqual(HealthJson, body);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
    }

    [TestMethod]
    public async Task Server_WithUri_AdvertisesGzipAndDecodesResponse()
    {
        using var server = new CompressingServer(HealthJson);
        using var horizon = new Server(server.Url.GetLeftPart(UriPartial.Authority));

        var health = await horizon.Health.Execute();

        Assert.IsTrue(health.CoreUp);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
    }

    [TestMethod]
    public async Task Server_WithBearerToken_AdvertisesGzipAndDecodesResponse()
    {
        using var server = new CompressingServer(HealthJson);
        using var horizon = new Server(server.Url.GetLeftPart(UriPartial.Authority), "token");

        var health = await horizon.Health.Execute();

        Assert.IsTrue(health.CoreUp);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
    }

    [TestMethod]
    public async Task StellarRpcServer_WithUri_AdvertisesGzipAndDecodesResponse()
    {
        using var server = new CompressingServer(RpcHealthJson);
        using var rpc = new StellarRpcServer(server.Url.GetLeftPart(UriPartial.Authority));

        var health = await rpc.GetHealth();

        Assert.AreEqual("healthy", health.Status);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
    }

    [TestMethod]
    public async Task LinkFollow_WithoutHttpClient_AdvertisesGzipAndDecodesResponse()
    {
        using var server = new CompressingServer(HealthJson);
        var link = new Link<HealthResponse> { Href = server.Url.ToString() };

        var health = await link.Follow();

        Assert.IsTrue(health.CoreSynced);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
    }

    [TestMethod]
    public async Task CreateDefaultHandler_WithoutRedirects_DecodesResponseAndDoesNotFollowRedirects()
    {
        using var server = new CompressingServer(HealthJson);
        using var client = new HttpClient(DefaultStellarSdkHttpClient.CreateDefaultHandler(false));

        var ok = await client.GetStringAsync(server.Url);
        var redirect = await client.GetAsync(new Uri(server.Url, "/redirect"));

        Assert.AreEqual(HealthJson, ok);
        StringAssert.Contains(server.RequestHeaders.ToString(), "gzip");
        Assert.AreEqual(HttpStatusCode.Redirect, redirect.StatusCode);
    }

    [TestMethod]
    public async Task Server_WithCustomHttpClient_IsLeftAsIs()
    {
        using var server = new CompressingServer(HealthJson);
        using var httpClient = new HttpClient();
        using var horizon = new Server(server.Url.GetLeftPart(UriPartial.Authority), httpClient);

        var health = await horizon.Health.Execute();

        Assert.IsTrue(health.CoreUp);
        Assert.IsFalse(server.RequestHeaders.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     A raw HTTP/1.1 server on 127.0.0.1 that gzips its body when the request allows it and records the
    ///     request headers; <c>/redirect</c> answers 302.
    /// </summary>
    private sealed class CompressingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _body;

        public CompressingServer(string body)
        {
            _body = body;
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public StringBuilder RequestHeaders { get; } = new();

        public void Dispose()
        {
            _listener.Stop();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream);
                    lock (RequestHeaders)
                    {
                        RequestHeaders.Append(request);
                    }

                    if (request.StartsWith("GET /redirect"))
                    {
                        await WriteAsync(stream,
                            "HTTP/1.1 302 Found\r\nLocation: /\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    var payload = Encoding.UTF8.GetBytes(_body);
                    var encoding = "";
                    if (request.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var compressed = new MemoryStream();
                        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
                        {
                            gzip.Write(payload, 0, payload.Length);
                        }

                        payload = compressed.ToArray();
                        encoding = "Content-Encoding: gzip\r\n";
                    }

                    await WriteAsync(stream,
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + encoding +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(payload, 0, payload.Length);
                }
                catch (Exception)
                {
                    // The client went away or the test tore the server down.
                }
            }
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var received = new StringBuilder();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return received.ToString();
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                var text = received.ToString();
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    continue;
                }

                var contentLength = 0;
                foreach (var line in text.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim());
                    }
                }

                if (text.Length >= headerEnd + 4 + contentLength)
                {
                    return text;
                }
            }
        }

        private static Task WriteAsync(NetworkStream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}
