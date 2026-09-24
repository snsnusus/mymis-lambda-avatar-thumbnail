# MyMIS.Lambda.AvatarThumbnail

An AWS Lambda function that generates thumbnails for Employee avatars
uploaded to `mymis-api`. Part of the MyMIS project.

## What this does

1. Triggered by an S3 `ObjectCreated` event when a new file lands under the
   `avatars/` prefix in the `mymis-uploads-bucket` S3 bucket.
2. Downloads the original image, resizes it to fit within 150×150px using
   ImageSharp (`ResizeMode.Max` — preserves aspect ratio, never upscales).
3. Uploads the resized thumbnail to the `avatars-thumbnails/` prefix in the
   same bucket.
4. Calls back to `mymis-api`'s internal endpoint
   (`PATCH /api/Employees/{id}/avatar-thumbnail`) to record the thumbnail's
   location.

## Project layout

This project is intentionally flat — there is no `src/` or `test/`
subfolder. Everything lives directly in the repo root:

- `Function.cs` — the handler
- `MyMIS.Lambda.AvatarThumbnail.csproj` — project file
- `aws-lambda-tools-defaults.json` — saved deploy settings (function name,
  IAM role, region)

## Prerequisites

- .NET 10 SDK
- The `Amazon.Lambda.Tools` global CLI tool:
  ```
  dotnet tool install -g Amazon.Lambda.Tools
  ```
- A SixLabors ImageSharp license file, `sixlabors.lic`, placed in this
  folder. **Not included in this repo** (gitignored — it's a personal,
  non-transferable credential). Obtain one at
  [sixlabors.com/pricing](https://sixlabors.com/pricing); the free
  "Hobbyist" tier covers this project.
- AWS CLI configured with credentials that can deploy Lambda functions.

## Build

```
dotnet build
```

## Deploy

```
dotnet lambda deploy-function mymis-avatar-thumbnail
```

Updates the existing `mymis-avatar-thumbnail` function in place.

## Configuration

Two environment variables, set on the Lambda function itself (not in this
repo):

| Variable | Purpose |
|---|---|
| `MYMIS_API_BASE_URL` | Base URL of the `mymis-api` instance to call back to |
| `INTERNAL_CALLBACK_SECRET` | Shared secret matching `mymis-api`'s `Internal:CallbackSecret` — must be identical on both sides |

Set via:
```
aws lambda update-function-configuration --function-name mymis-avatar-thumbnail --environment file://env.json
```
using a gitignored, local-only JSON file — never commit real secret values.

## IAM

Runs under `mymis-avatar-thumbnail-lambda-role`: `s3:GetObject` on
`avatars/*`, `s3:PutObject` on `avatars-thumbnails/*` only, plus
`AWSLambdaBasicExecutionRole` for CloudWatch logging.

## S3 trigger

Configured on `mymis-uploads-bucket`'s Event Notifications: all object
create events, prefix `avatars/`, destination this function. **Do not**
widen that prefix to include `avatars-thumbnails/` — this function writes
there, and doing so would cause it to re-trigger itself indefinitely.
