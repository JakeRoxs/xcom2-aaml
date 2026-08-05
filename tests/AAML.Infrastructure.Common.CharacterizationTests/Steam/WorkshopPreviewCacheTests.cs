using System.Net;
using System.Net.Http.Headers;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Steam;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Steam;

[TestClass]
public sealed class WorkshopPreviewCacheTests
{
    private string root = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "aaml-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task Download_WritesSupportedImageAndSubsequentRequestHitsCache()
    {
        var handler = new StubHandler(_ => Image(HttpStatusCode.OK, "image/png", [1, 2, 3]));
        var cache = Create(handler);

        var first = await cache.GetAsync(new WorkshopId(42), "https://cdn.example.test/image", TestContext.CancellationToken);
        var second = await cache.GetAsync(new WorkshopId(42), "https://cdn.example.test/image", TestContext.CancellationToken);

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().EndWith(".png").And.Be(second.Value);
        (await File.ReadAllBytesAsync(first.Value!, TestContext.CancellationToken)).Should().BeEquivalentTo([1, 2, 3]);
        handler.RequestCount.Should().Be(1);
        Directory.GetFiles(Path.GetDirectoryName(first.Value!)!, "*.tmp").Should().BeEmpty();
    }

    [TestMethod]
    public async Task InvalidUrl_IsRejectedWithoutNetworkOrFiles()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException());
        var result = await Create(handler).GetAsync(new WorkshopId(42), "http://cdn.example.test/image.png", TestContext.CancellationToken);

        result.Error!.Code.Should().Be("workshop.preview_url_invalid");
        handler.RequestCount.Should().Be(0);
        Directory.Exists(Path.Combine(root, "WorkshopPreviews")).Should().BeFalse();
    }

    [TestMethod]
    public async Task OversizedContentLength_IsRejectedWithoutCachedFile()
    {
        var content = new ByteArrayContent([1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Headers.ContentLength = 5L * 1024 * 1024 + 1;
        var result = await Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }))
            .GetAsync(new WorkshopId(42), "https://cdn.example.test/image", TestContext.CancellationToken);

        result.Error!.Code.Should().Be("workshop.preview_too_large");
        CachedFiles().Should().BeEmpty();
    }

    [TestMethod]
    public async Task OversizedStream_IsRejectedAndTemporaryFileIsRemoved()
    {
        var bytes = new byte[5 * 1024 * 1024 + 1];
        var content = new StreamContent(new NonSeekableStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        var result = await Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }))
            .GetAsync(new WorkshopId(42), "https://cdn.example.test/image", TestContext.CancellationToken);

        result.Error!.Code.Should().Be("workshop.preview_too_large");
        CachedFiles().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("text/plain")]
    [DataRow("image/svg+xml")]
    public async Task UnsupportedContentType_IsRejected(string contentType)
    {
        var result = await Create(new StubHandler(_ => Image(HttpStatusCode.OK, contentType, [1])))
            .GetAsync(new WorkshopId(42), "https://cdn.example.test/image", TestContext.CancellationToken);

        result.Error!.Code.Should().Be("workshop.preview_content_type_invalid");
        CachedFiles().Should().BeEmpty();
    }

    [TestMethod]
    public async Task HttpFailure_IsStructured()
    {
        var result = await Create(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .GetAsync(new WorkshopId(42), "https://cdn.example.test/image", TestContext.CancellationToken);

        result.Error!.Code.Should().Be("workshop.preview_http_failed");
    }

    [TestMethod]
    public async Task CallerCancellation_IsStructuredAndLeavesNoFiles()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await Create(new StubHandler(_ => throw new InvalidOperationException()))
            .GetAsync(new WorkshopId(42), "https://cdn.example.test/image", source.Token);

        result.Error!.Kind.Should().Be(AAML.Application.Common.ErrorKind.Cancelled);
        CachedFiles().Should().BeEmpty();
    }

    public TestContext TestContext { get; set; }

    private WorkshopPreviewCache Create(HttpMessageHandler handler) => new(new TestPaths(root), new HttpClient(handler));
    private string[] CachedFiles() => Directory.Exists(Path.Combine(root, "WorkshopPreviews")) ? Directory.GetFiles(Path.Combine(root, "WorkshopPreviews")) : [];
    private static HttpResponseMessage Image(HttpStatusCode status, string contentType, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private sealed record TestPaths(string CacheDirectory) : IApplicationPaths
    {
        public string ConfigurationDirectory => CacheDirectory;
        public string DataDirectory => CacheDirectory;
        public string StateDirectory => CacheDirectory;
        public string RuntimeDirectory => CacheDirectory;
    }
}
