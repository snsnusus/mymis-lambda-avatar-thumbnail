using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MyMIS.Lambda.AvatarThumbnail;

public class Function
{
  private const int ThumbnailMaxDimension = 150;
  private readonly IAmazonS3 _s3Client;
  private readonly HttpClient _httpClient;

  // Called by the Lambda runtime itself — must stay public and parameterless.
  public Function() : this(new AmazonS3Client(), new HttpClient())
  {
  }

  // Called only from unit tests, to substitute fakes for both AWS and HTTP.
  internal Function(IAmazonS3 s3Client, HttpClient httpClient)
  {
    _s3Client = s3Client;
    _httpClient = httpClient;
  }

  public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
  {
    foreach (var record in s3Event.Records)
    {
      var bucketName = record.S3.Bucket.Name;
      var key = System.Net.WebUtility.UrlDecode(record.S3.Object.Key);

      context.Logger.LogInformation($"Processing {key}");

      using var originalStream = new MemoryStream();
      using (var getResponse = await _s3Client.GetObjectAsync(new GetObjectRequest
      {
        BucketName = bucketName,
        Key = key
      }))
      {
        await getResponse.ResponseStream.CopyToAsync(originalStream);
      }
      originalStream.Position = 0;

      using var image = await Image.LoadAsync(originalStream);

      image.Mutate(x => x.Resize(new ResizeOptions
      {
        Size = new Size(ThumbnailMaxDimension, ThumbnailMaxDimension),
        Mode = ResizeMode.Max
      }));

      using var thumbnailStream = new MemoryStream();
      await image.SaveAsJpegAsync(thumbnailStream);
      thumbnailStream.Position = 0;

      var fileName = key["avatars/".Length..];
      var thumbnailKey = $"avatars-thumbnails/{fileName}";

      await _s3Client.PutObjectAsync(new PutObjectRequest
      {
        BucketName = bucketName,
        Key = thumbnailKey,
        InputStream = thumbnailStream,
        ContentType = "image/jpeg"
      });

      context.Logger.LogInformation($"Thumbnail written to {thumbnailKey}");

      var employeeIdText = Path.GetFileNameWithoutExtension(fileName);
      if (int.TryParse(employeeIdText, out var employeeId))
      {
        await NotifyApiAsync(employeeId, thumbnailKey, context);
      }
      else
      {
        context.Logger.LogError($"Could not parse employee id from key: {key}");
      }
    }
  }

  private async Task NotifyApiAsync(int employeeId, string thumbnailKey, ILambdaContext context)
  {
    var apiBaseUrl = Environment.GetEnvironmentVariable("MYMIS_API_BASE_URL");
    var internalSecret = Environment.GetEnvironmentVariable("INTERNAL_CALLBACK_SECRET");

    if (string.IsNullOrEmpty(apiBaseUrl) || string.IsNullOrEmpty(internalSecret))
    {
      context.Logger.LogError("Missing MYMIS_API_BASE_URL or INTERNAL_CALLBACK_SECRET environment variable — skipping callback.");
      return;
    }

    var url = $"{apiBaseUrl}/api/Employees/{employeeId}/avatar-thumbnail";
    var payload = JsonSerializer.Serialize(new { thumbnailKey });

    using var request = new HttpRequestMessage(HttpMethod.Patch, url)
    {
      Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    request.Headers.Add("X-Internal-Secret", internalSecret);

    try
    {
      using var response = await _httpClient.SendAsync(request);

      if (response.IsSuccessStatusCode)
      {
        context.Logger.LogInformation($"Notified mymis-api for employee {employeeId}: {(int)response.StatusCode}");
      }
      else
      {
        var body = await response.Content.ReadAsStringAsync();
        context.Logger.LogError($"mymis-api callback failed for employee {employeeId}: {(int)response.StatusCode} {body}");
      }
    }
    catch (Exception ex)
    {
      // Caught deliberately, not rethrown — see the log-and-continue decision above.
      context.Logger.LogError($"mymis-api callback threw for employee {employeeId}: {ex.Message}");
    }
  }
}