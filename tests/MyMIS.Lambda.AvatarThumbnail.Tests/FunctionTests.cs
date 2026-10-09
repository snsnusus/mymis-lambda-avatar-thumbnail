using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.S3Events;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MyMIS.Lambda.AvatarThumbnail.Tests;

// The function reads two environment variables, and environment variables are shared by
// the whole process, so every test that touches them lives in this one class.
public class FunctionTests : IDisposable
{
  private const string ApiBaseUrl = "https://api.example.test";
  private const string Secret = "test-secret";

  private readonly Mock<IAmazonS3> _s3 = new();
  private readonly RecordingHandler _handler = new();

  // What the function asked S3 for, and what it uploaded back.
  private readonly List<GetObjectRequest> _downloads = [];
  private readonly List<(string Key, string? ContentType, byte[] Body)> _uploads = [];

  // The "original photo" S3 hands back. Each test sets it before calling the function.
  private byte[] _originalImage = [];

  public FunctionTests()
  {
    Environment.SetEnvironmentVariable("MYMIS_API_BASE_URL", ApiBaseUrl);
    Environment.SetEnvironmentVariable("INTERNAL_CALLBACK_SECRET", Secret);

    _s3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
      .Callback<GetObjectRequest, CancellationToken>((request, _) => _downloads.Add(request))
      .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(_originalImage) });

    _s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
      .Callback<PutObjectRequest, CancellationToken>((request, _) =>
      {
        // Copy the bytes now: the function disposes the stream right after the call returns.
        using var copy = new MemoryStream();
        request.InputStream.CopyTo(copy);
        _uploads.Add((request.Key, request.ContentType, copy.ToArray()));
      })
      .ReturnsAsync(new PutObjectResponse());
  }

  public void Dispose()
  {
    Environment.SetEnvironmentVariable("MYMIS_API_BASE_URL", null);
    Environment.SetEnvironmentVariable("INTERNAL_CALLBACK_SECRET", null);
    GC.SuppressFinalize(this);
  }

  // ---------------- helpers ----------------

  private static async Task<byte[]> NewPngAsync(int width, int height)
  {
    using var image = new Image<Rgba32>(width, height);
    using var stream = new MemoryStream();
    await image.SaveAsPngAsync(stream);
    return stream.ToArray();
  }

  // Builds the JSON S3 sends to Lambda and reads it with the same serializer Lambda uses,
  // so the test doesn't depend on how the S3Event classes are constructed.
  private static S3Event NewEvent(params string[] keys)
  {
    var payload = new
    {
      Records = keys.Select(key => new
      {
        s3 = new
        {
          bucket = new { name = "test-bucket" },
          @object = new { key },
        },
      }).ToArray(),
    };

    var json = JsonSerializer.Serialize(payload);
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    return new DefaultLambdaJsonSerializer().Deserialize<S3Event>(stream);
  }

  private async Task<TestLambdaContext> RunAsync(params string[] keys)
  {
    var function = new Function(_s3.Object, new HttpClient(_handler));
    var context = new TestLambdaContext();
    await function.FunctionHandler(NewEvent(keys), context);
    return context;
  }

  private static string LogOf(TestLambdaContext context) =>
    ((TestLambdaLogger)context.Logger).Buffer.ToString();

  // Stands in for mymis-api: records what it was sent and answers with a canned result.
  private sealed class RecordingHandler : HttpMessageHandler
  {
    public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;
    public Exception? ToThrow { get; set; }
    public List<(HttpMethod Method, string? Url, string? Secret, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
      var secret = request.Headers.TryGetValues("X-Internal-Secret", out var values) ? values.Single() : null;
      Requests.Add((request.Method, request.RequestUri?.ToString(), secret, body));

      if (ToThrow is not null) throw ToThrow;
      return new HttpResponseMessage(Status) { Content = new StringContent("") };
    }
  }

  // ---------------- the thumbnail itself ----------------

  [Fact]
  public async Task FunctionHandler_PngUpload_WritesAJpegThumbnailUnderTheThumbnailPrefix()
  {
    // Arrange
    _originalImage = await NewPngAsync(400, 300);

    // Act
    await RunAsync("avatars/5.png");

    // Assert: the thumbnail keeps the original file name (even the .png extension) but the
    // bytes and the content type are JPEG.
    var upload = Assert.Single(_uploads);
    Assert.Equal("avatars-thumbnails/5.png", upload.Key);
    Assert.Equal("image/jpeg", upload.ContentType);
    Assert.Equal("JPEG", Image.DetectFormat(upload.Body).Name);
  }

  [Theory]
  [InlineData(400, 300)]
  [InlineData(300, 400)]
  [InlineData(800, 800)]
  public async Task FunctionHandler_LargeImage_ShrinksSoTheLongestSideIs150AndTheAspectRatioIsKept(int width, int height)
  {
    // Arrange
    _originalImage = await NewPngAsync(width, height);

    // Act
    await RunAsync("avatars/5.png");

    // Assert
    var info = Image.Identify(Assert.Single(_uploads).Body);
    Assert.Equal(150, Math.Max(info.Width, info.Height));

    var expectedRatio = (double)width / height;
    Assert.InRange((double)info.Width / info.Height, expectedRatio - 0.02, expectedRatio + 0.02);
  }

  [Fact]
  public async Task FunctionHandler_ImageSmallerThanTheLimit_IsNotEnlarged()
  {
    // Arrange: your notes say ResizeMode.Max never upscales. This test checks that claim.
    // If it fails, the notes were wrong and the Function is enlarging small photos.
    _originalImage = await NewPngAsync(50, 40);

    // Act
    await RunAsync("avatars/5.png");

    // Assert
    var info = Image.Identify(Assert.Single(_uploads).Body);
    Assert.Equal(50, info.Width);
    Assert.Equal(40, info.Height);
  }

  [Fact]
  public async Task FunctionHandler_UrlEncodedKey_IsDecodedBeforeDownloading()
  {
    // Arrange: S3 events send object keys URL-encoded ("%2E" is a dot)
    _originalImage = await NewPngAsync(100, 100);

    // Act
    await RunAsync("avatars/5%2Epng");

    // Assert
    var download = Assert.Single(_downloads);
    Assert.Equal("test-bucket", download.BucketName);
    Assert.Equal("avatars/5.png", download.Key);
    Assert.Equal("avatars-thumbnails/5.png", Assert.Single(_uploads).Key);
  }

  [Fact]
  public async Task FunctionHandler_SeveralRecords_ProcessesEachOne()
  {
    // Arrange
    _originalImage = await NewPngAsync(300, 300);

    // Act
    await RunAsync("avatars/5.png", "avatars/6.png");

    // Assert
    Assert.Equal(
      new[] { "avatars-thumbnails/5.png", "avatars-thumbnails/6.png" },
      _uploads.Select(u => u.Key));
    Assert.Equal(2, _handler.Requests.Count);
  }

  // ---------------- the callback to mymis-api ----------------

  [Fact]
  public async Task FunctionHandler_AfterWritingTheThumbnail_PatchesMymisApiWithTheSecretAndTheKey()
  {
    // Arrange
    _originalImage = await NewPngAsync(400, 300);

    // Act
    await RunAsync("avatars/5.png");

    // Assert
    var request = Assert.Single(_handler.Requests);
    Assert.Equal(HttpMethod.Patch, request.Method);
    Assert.Equal($"{ApiBaseUrl}/api/Employees/5/avatar-thumbnail", request.Url);
    Assert.Equal(Secret, request.Secret);

    using var body = JsonDocument.Parse(request.Body);
    Assert.Equal("avatars-thumbnails/5.png", body.RootElement.GetProperty("thumbnailKey").GetString());
  }

  [Fact]
  public async Task FunctionHandler_ApiRejectsTheCallback_LogsItAndDoesNotThrow()
  {
    // Arrange: wrong secret, API down, etc. Rethrowing would make Lambda retry the whole job
    // (download and resize again) just to repeat one HTTP call, so the function logs and moves on.
    _originalImage = await NewPngAsync(400, 300);
    _handler.Status = HttpStatusCode.Unauthorized;

    // Act
    var context = await RunAsync("avatars/5.png");

    // Assert: the thumbnail was still written
    Assert.Single(_uploads);
    Assert.Contains("callback failed for employee 5", LogOf(context));
  }

  [Fact]
  public async Task FunctionHandler_CallbackThrowsANetworkError_LogsItAndDoesNotThrow()
  {
    // Arrange
    _originalImage = await NewPngAsync(400, 300);
    _handler.ToThrow = new HttpRequestException("connection refused");

    // Act
    var context = await RunAsync("avatars/5.png");

    // Assert
    Assert.Single(_uploads);
    Assert.Contains("callback threw for employee 5", LogOf(context));
  }

  [Fact]
  public async Task FunctionHandler_EnvironmentVariablesMissing_SkipsTheCallbackAndLogsIt()
  {
    // Arrange
    _originalImage = await NewPngAsync(400, 300);
    Environment.SetEnvironmentVariable("MYMIS_API_BASE_URL", null);
    Environment.SetEnvironmentVariable("INTERNAL_CALLBACK_SECRET", null);

    // Act
    var context = await RunAsync("avatars/5.png");

    // Assert
    Assert.Single(_uploads);
    Assert.Empty(_handler.Requests);
    Assert.Contains("Missing MYMIS_API_BASE_URL", LogOf(context));
  }

  [Fact]
  public async Task FunctionHandler_FileNameIsNotAnEmployeeId_WritesTheThumbnailButSkipsTheCallback()
  {
    // Arrange
    _originalImage = await NewPngAsync(400, 300);

    // Act
    var context = await RunAsync("avatars/abc.png");

    // Assert
    Assert.Single(_uploads);
    Assert.Empty(_handler.Requests);
    Assert.Contains("Could not parse employee id", LogOf(context));
  }

  // ---------------- bad input ----------------

  [Fact]
  public async Task FunctionHandler_FileIsNotAnImage_Throws_AndWritesNothing()
  {
    // Arrange
    _originalImage = Encoding.UTF8.GetBytes("this is not an image");

    // Act + Assert: the exception reaches Lambda, which reports the failure (and may retry)
    await Assert.ThrowsAnyAsync<Exception>(() => RunAsync("avatars/5.png"));

    Assert.Empty(_uploads);
    Assert.Empty(_handler.Requests);
  }
}