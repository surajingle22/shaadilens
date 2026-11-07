using System.Net;
using System.Text;
using FluentAssertions;
using ShaadiLens.Infrastructure.FaceService;

namespace ShaadiLens.Tests.Faces;

/// <summary>Verifies the JSON contract between face-service (Python, snake_case) and .NET.</summary>
public sealed class FaceServiceClientTests
{
    // Trimmed copy of a real /v1/analyze response (embedding shortened to 4 values).
    private const string AnalyzeJson = """
        {
          "width": 1280, "height": 886, "elapsed_ms": 412.3,
          "faces": [{
            "bbox": {"x": 100.5, "y": 50.0, "w": 80.0, "h": 96.0},
            "kps": [[120,80],[150,80],[135,95],[122,110],[148,110]],
            "det_score": 0.87,
            "embedding": [0.5, 0.5, 0.5, 0.5],
            "pose": {"yaw": -12.5, "pitch": 3.0, "roll": 1.0},
            "face_px": 80.0,
            "blur": 154.2
          }]
        }
        """;

    [Fact]
    public async Task AnalyzeAsync_maps_snake_case_response()
    {
        var handler = new StubHandler(HttpStatusCode.OK, AnalyzeJson);
        var client = new FaceServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://face/") });

        var result = await client.AnalyzeAsync(new MemoryStream([1, 2, 3]), "a.jpg");

        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://face/v1/analyze");
        result.Width.Should().Be(1280);
        result.ElapsedMs.Should().Be(412.3);
        var face = result.Faces.Should().ContainSingle().Subject;
        face.BBox.Should().Be(new Application.Faces.FaceBox(100.5f, 50f, 80f, 96f));
        face.DetScore.Should().BeApproximately(0.87f, 1e-4f);
        face.FacePx.Should().Be(80);
        face.Pose.Yaw.Should().Be(-12.5f);
        face.Kps.Should().HaveCount(5);
        face.Embedding.Should().Equal(0.5f, 0.5f, 0.5f, 0.5f);
    }

    [Fact]
    public async Task AnalyzeAsync_throws_with_status_on_bad_image()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"detail":"Not a supported image"}""");
        var client = new FaceServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://face/") });

        var act = () => client.AnalyzeAsync(new MemoryStream([1]), "x.txt");

        (await act.Should().ThrowAsync<FaceServiceException>()).Which.StatusCode.Should().Be(400);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
