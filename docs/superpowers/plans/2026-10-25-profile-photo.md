# Profile Photo Upload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A signed-in user can add, change and remove a cropped profile photo from their own badge, stored in a private S3 bucket and served through CloudFront.

**Architecture:** The browser crops to a square and uploads it; the API re-encodes it with ImageSharp to 640×640 WebP, writes it to S3 under an immutable key, stores the public URL in `User.AvatarUrl`, and deletes the previous object. Production serves `/avatars/*` from the bucket via a CloudFront origin with OAC; local dev and integration tests use Adobe S3Mock.

**Tech Stack:** ASP.NET Core 9, EF Core, AWSSDK.S3 4.x, SixLabors.ImageSharp, Testcontainers, Angular (standalone, signals), AWS CDK (C#).

**Spec:** `docs/superpowers/specs/2026-10-25-profile-photo-design.md`

## Global Constraints

- **#128 gate:** the branch may be built and reviewed now, but it does **not merge** until #128 has stated the EC2 metadata hop limit in code. If #128 can't do that without replacing the host, stop and ask the user.
- Stored image: **640×640 WebP**, re-encoded server-side, no EXIF/ICC/XMP metadata.
- Accepted upload types: **JPEG, PNG, WebP** (decided by decoding, never by the client's content type).
- Upload body limit **5 MB** (5,000,000 bytes), enforced before decoding; decoded source limited to **8000 px per side**.
- Object keys: `avatars/{userId}/{random}.webp`, never reused. Content type `image/webp`.
- Bucket: private, `BlockPublicAccess.BLOCK_ALL`, S3-managed encryption, `EnforceSSL`.
- CloudFront `/avatars/*`: OAC origin, edge cache max TTL **24 hours**. No `cloudfront:CreateInvalidation`.
- Host role on the avatars bucket: **only** `s3:PutObject` and `s3:DeleteObject` on `avatars/*`.
- Photos are always public (no privacy filtering). Upload/remove require a signed-in account.
- Admin removal is out of scope.
- S3Mock image: `adobe/s3mock:5.2.3` in both `docker-compose.yml` and the Testcontainer.
- Tunable values live in options (CLAUDE.md §7); endpoints idempotent; DTOs at boundaries; sentence-case UI copy; Conventional Commits; no AI attribution lines; each commit confirmed with the user before it is made.

## Review Focus

1. **iPhone HEIC or any file the browser can't decode** → the crop dialog says "That file type isn't supported. Choose a JPEG, PNG or WebP." instead of showing a blank stage (Task 4).
2. **A large original phone photo (e.g. 12 MB, 48 MP)** → accepted by the picker; only the cropped 640 px export is uploaded, so the 5 MB limit never applies to the original (Task 4).
3. **A sideways-stored JPEG with an EXIF orientation tag** → appears upright in the crop dialog and upright in the stored WebP (Tasks 1 and 4).
4. **A transparent PNG** → keeps its transparency in the stored WebP rather than gaining a black background (Task 1).
5. **Double-clicking "Save photo"** → one upload; the button is disabled while busy (Task 5).

---

### Task 1: Avatar image processing

**Files:**
- Create: `src/Marquee.Infrastructure/Avatars/AvatarOptions.cs`
- Create: `src/Marquee.Infrastructure/Avatars/AvatarImage.cs`
- Modify: `src/Marquee.Infrastructure/Marquee.Infrastructure.csproj` (add `SixLabors.ImageSharp`)
- Modify: `src/Marquee.Infrastructure/DependencyInjection.cs` (bind `AvatarOptions`)
- Modify: `src/Marquee.Api/appsettings.json` (an `Avatars` section with the defaults below)
- Test: `tests/Marquee.UnitTests/AvatarImageTests.cs`

**Interfaces:**
- Produces:
  - `AvatarOptions` (section `"Avatars"`): `string BucketName = ""`, `string PublicBaseUrl = ""`, `string? ServiceUrl`, `int Size = 640`, `int MaxUploadBytes = 5_000_000`, `int MaxSourceDimension = 8000`, `int Quality = 80`.
  - `static class AvatarImage` with `Task<byte[]> ToWebpAsync(Stream source, AvatarOptions options, CancellationToken ct)`. Throws `AvatarImageException(string message)` with a sentence-case message on any refusal.

- [ ] **Step 1: Write the failing tests** in `AvatarImageTests` (build inputs in-test with ImageSharp):
  - `Jpeg_becomes_640_square_webp`: 1000×1000 JPEG → decoded output format is WebP, `Width == 640 && Height == 640`.
  - `Non_square_input_is_centre_cropped`: 1200×800 → 640×640.
  - `Metadata_is_stripped`: JPEG with an EXIF GPS profile → output `Metadata.ExifProfile is null`, and no ICC/XMP profile.
  - `Exif_orientation_is_applied`: 400×200 JPEG with orientation tag 6 (rotate 90°) whose left half is red, right half blue → output's top-centre pixel is red (upright).
  - `Transparent_png_keeps_alpha`: fully transparent 100×100 PNG → output pixel (0,0) has alpha 0.
  - `Gif_is_refused`: GIF → `AvatarImageException` "That file type isn't supported. Use a JPEG, PNG or WebP."
  - `Garbage_is_refused`: random bytes → same exception and message.
  - `Huge_dimensions_are_refused`: PNG header claiming 9000×100 → `AvatarImageException` "That image is too large." (checked via `Image.IdentifyAsync` before full decode).

- [ ] **Step 2: Run** `dotnet test tests/Marquee.UnitTests --filter AvatarImageTests` → FAIL (types missing).

- [ ] **Step 3: Implement.** `IdentifyAsync` first (format allowlist by `DecodedImageFormat`, dimension cap), then `LoadAsync`, `AutoOrient()`, `Resize(new ResizeOptions { Size = new(size, size), Mode = ResizeMode.Crop })`, clear `Metadata.ExifProfile/IccProfile/XmpProfile`, save with `WebpEncoder { Quality = options.Quality }`.

- [ ] **Step 4: Run** the filter again → PASS. Run the full unit suite → PASS.

- [ ] **Step 5: Commit** `feat: re-encode uploaded photos to 640px WebP (#63)`.

---

### Task 2: S3 avatar store, S3Mock locally and in tests

**Files:**
- Create: `src/Marquee.Infrastructure/Avatars/IAvatarStore.cs`, `S3AvatarStore.cs`
- Modify: `src/Marquee.Infrastructure/Marquee.Infrastructure.csproj` (add `AWSSDK.S3` `4.*`)
- Modify: `src/Marquee.Infrastructure/DependencyInjection.cs` (register `IAmazonS3`, `IAvatarStore`)
- Modify: `docker-compose.yml` (service `s3`), `src/Marquee.Api/appsettings.Development.json` (`Avatars` values), `README.md` (S3Mock in the run section's service list)
- Create: `tests/Marquee.IntegrationTests/Support/S3MockContainer.cs`
- Modify: `tests/Marquee.IntegrationTests/Support/MarqueeAppFactory.cs` (start it, add its settings)
- Test: `tests/Marquee.IntegrationTests/Profile/AvatarStoreTests.cs`

**Interfaces:**
- Consumes: `AvatarOptions` (Task 1).
- Produces:
  - `IAvatarStore`:
    - `Task<string> PutAsync(Guid userId, byte[] webp, CancellationToken ct)` → returns the **public URL** (`PublicBaseUrl` trimmed of `/` + `/` + key).
    - `Task DeleteAsync(string publicUrl, CancellationToken ct)` → deletes the key derived by stripping `PublicBaseUrl + "/"`; does nothing if the URL doesn't start with it.
  - `MarqueeAppFactory.S3` (`S3MockContainer`) with `Task<bool> ExistsAsync(string key)` and `Task<byte[]> GetAsync(string key)` for assertions.
- Registration: `IAmazonS3` singleton. Production (`ServiceUrl` empty) uses the default credential chain and region `ca-central-1`; otherwise `BasicAWSCredentials("local", "local")`, `ServiceURL = ServiceUrl`, `ForcePathStyle = true` (same pattern as the Cognito client in `AuthenticationRegistration.cs:63-77`).

- [ ] **Step 1: Add the compose service.** `s3`: image `adobe/s3mock:5.2.3`, port `9090:9090`, env var that creates bucket `marquee-avatars` at startup (check the exact variable name in the image's README for 5.x before writing it), healthcheck on port 9090. Development settings: `Avatars:BucketName=marquee-avatars`, `Avatars:ServiceUrl=http://localhost:9090`, `Avatars:PublicBaseUrl=http://localhost:9090/marquee-avatars`.
- [ ] **Step 2: Verify** `docker compose up -d --wait s3` then `curl -s -o /dev/null -w "%{http_code}" http://localhost:9090/marquee-avatars` → `200`.
- [ ] **Step 3: Write `S3MockContainer`** in the `CognitoLocal` style: free host port chosen first, same image, same bucket env var, wait on HTTP; factory sets `Avatars__ServiceUrl`, `Avatars__BucketName`, `Avatars__PublicBaseUrl` from it.
- [ ] **Step 4: Write the failing tests** in `AvatarStoreTests`:
  - `Put_writes_webp_under_user_prefix`: URL starts with `PublicBaseUrl + "/avatars/{userId}/"` and ends `.webp`; object exists; its content type is `image/webp`.
  - `Two_puts_use_different_keys`.
  - `Delete_removes_the_object`.
  - `Delete_ignores_foreign_urls`: `DeleteAsync("https://elsewhere.test/x.webp")` → no exception, other objects untouched.
- [ ] **Step 5: Run** `dotnet test tests/Marquee.IntegrationTests --filter AvatarStoreTests` → FAIL.
- [ ] **Step 6: Implement `S3AvatarStore`** (key `avatars/{userId}/{Guid.NewGuid():N}.webp`, `PutObjectRequest` with `ContentType = "image/webp"` and `Headers.CacheControl = "public, max-age=86400"`).
- [ ] **Step 7: Run** the filter → PASS; full integration suite → PASS (`FixtureSanityTests` still green).
- [ ] **Step 8: Commit** `feat: store avatars in S3, with S3Mock for local runs and tests (#63)`.

---

### Task 3: Upload and remove endpoints

**Files:**
- Create: `src/Marquee.Api/Services/AvatarService.cs`
- Modify: `src/Marquee.Api/ApiServiceRegistration.cs` (register `IAvatarService`)
- Modify: `src/Marquee.Api/Controllers/UsersController.cs` (two actions)
- Modify: `src/Marquee.Api/Security/RateLimitOptions.cs` (`RateLimitPolicies.AvatarUpload = "avatar-upload"`, rule `AvatarUpload = new() { PermitLimit = 10, WindowSeconds = 600 }`)
- Modify: `src/Marquee.Api/Security/RateLimitingRegistration.cs` (policy via `Partition`)
- Modify: `src/Marquee.Api/Program.cs` (`RequireKeys` in Production gains `"Avatars:BucketName"`, `"Avatars:PublicBaseUrl"`)
- Test: `tests/Marquee.IntegrationTests/Profile/AvatarEndpointTests.cs`

**Interfaces:**
- Consumes: `AvatarImage.ToWebpAsync`, `IAvatarStore` (Tasks 1–2).
- Produces:
  - `PUT /api/users/me/avatar` — `[Authorize]`, `[EnableRateLimiting(RateLimitPolicies.AvatarUpload)]`, `[RequestSizeLimit(5_000_000)]` (also checked against `AvatarOptions.MaxUploadBytes`), multipart field **`file`** → `200 UserDto` with the new `AvatarUrl`; `400 { error }` with the `AvatarImageException` message; `413` over the limit.
  - `DELETE /api/users/me/avatar` — `[Authorize]` → `200 UserDto` with `AvatarUrl = null`; succeeds when there is no photo.
  - `IAvatarService`: `Task<UserDto?> ReplaceAsync(Guid userId, Stream file, CancellationToken ct)`, `Task<UserDto?> RemoveAsync(Guid userId, CancellationToken ct)`.
- Replace order: process → `PutAsync` → update row and save → `DeleteAsync(old)`; a failed delete is logged as a warning and not rethrown.

- [ ] **Step 1: Write the failing tests:**
  - `Upload_sets_avatar_url_and_stores_object` (also `GET /api/auth/me` shows the same URL).
  - `Replace_deletes_previous_object`.
  - `Remove_clears_url_and_deletes_object`.
  - `Remove_without_photo_is_ok` (200, `AvatarUrl` null).
  - `Anonymous_upload_is_401` (with and without an anonymous session header).
  - `Non_image_is_400_with_message`.
  - `Oversize_is_413` (5,000,001 bytes).
  - `Stored_object_has_no_exif` (upload a JPEG with GPS EXIF, fetch via `factory.S3.GetAsync`, assert no EXIF).
  - `Photo_shows_on_private_profile_for_strangers`: private account uploads; another user's restricted `GET /api/users/{username}` payload carries the `avatarUrl` (the restricted DTO in `SocialDtos.cs` already includes it; this pins it).
- [ ] **Step 2: Run** `--filter AvatarEndpointTests` → FAIL.
- [ ] **Step 3: Implement** service, actions, rate-limit policy, `RequireKeys` entries.
- [ ] **Step 4: Run** the filter → PASS; full integration and unit suites → PASS.
- [ ] **Step 5: Commit** `feat: upload, replace and remove your profile photo (#63)`.

---

### Task 4: Crop dialog

**Files:**
- Create: `src/Marquee.Web/src/app/core/util/crop.ts` (+ `crop.spec.ts`)
- Create: `src/Marquee.Web/src/app/shared/avatar-crop-dialog.component.{ts,html,css,spec.ts}`

**Interfaces:**
- Produces:
  - `cropSource(natural: { width: number; height: number }, frame: number, zoom: number, offset: { x: number; y: number }): { sx: number; sy: number; sw: number; sh: number }` — maps the on-screen square frame (`frame` CSS px) to the source rectangle. At `zoom = 1` the image's short side fills the frame (cover); `offset` is the image's displacement in frame px and is clamped so the frame never shows outside the image.
  - `<app-avatar-crop-dialog [file]="File" (saved)="Blob" (cancelled)="void">`: title "Place your photo", hint "Drag to move. The dashed circle is what small round versions show.", square frame with dimmed outside, dashed circle guide (dialog only), zoom slider 1–3, buttons "Cancel" / "Save photo". `saved` emits a 640×640 `image/webp` Blob (fallback `image/jpeg` where `toBlob` can't encode WebP). Escape cancels; the slider and buttons are keyboard reachable; arrow keys nudge the photo while the frame has focus.
- No crop library: a hand-rolled canvas is enough here. If it isn't, measure before adding one (memory: measure refactors).

- [ ] **Step 1: Write failing `crop.spec.ts`:**
  - landscape 2000×1000, frame 200, zoom 1, offset 0 → `{ sx: 500, sy: 0, sw: 1000, sh: 1000 }`.
  - same, zoom 2 → `{ sx: 750, sy: 250, sw: 500, sh: 500 }`.
  - offset pushed past the edge is clamped (sx never < 0, sx + sw never > width).
  - portrait 1000×2000 mirrors the landscape case.
- [ ] **Step 2: Write failing component specs:**
  - renders the title, hint, dashed guide and both buttons;
  - a file that fails to load shows "That file type isn't supported. Choose a JPEG, PNG or WebP." and disables "Save photo" (Review Focus 1);
  - a 12 MB file is accepted (no size check on the original) (Review Focus 2);
  - Escape emits `cancelled`.
- [ ] **Step 3: Run** `npx ng test --watch=false` → the new specs FAIL.
- [ ] **Step 4: Implement** `crop.ts` and the dialog (load via `createImageBitmap(file, { imageOrientation: 'from-image' })` so EXIF orientation is honoured (Review Focus 3), draw with `cropSource` into a 640×640 canvas).
- [ ] **Step 5: Run** the full frontend suite → PASS.
- [ ] **Step 6: Commit** `feat: crop dialog with a round-preview guide (#63)`.

---

### Task 5: Photo controls on your own badge

**Files:**
- Modify: `src/Marquee.Web/src/app/core/api/users.service.ts` (+ spec)
- Modify: `src/Marquee.Web/src/app/shared/id-badge.component.{ts,html,css,spec.ts}`
- Modify: `src/Marquee.Web/src/app/features/profile/profile.component.{ts,html,spec.ts}`

**Interfaces:**
- Consumes: the endpoints (Task 3), `<app-avatar-crop-dialog>` (Task 4), `AuthService.applyProfileUpdate(user)` (already exists, used by `profile.component.ts` `patch`).
- Produces:
  - `UsersService.uploadAvatar(blob: Blob): Observable<UserDto>` (PUT `me/avatar`, `FormData` field `file`), `removeAvatar(): Observable<UserDto>` (DELETE).
  - `IdBadgeComponent`: new `editable = input(false)`, outputs `changePhoto` and `removePhoto`. When editable, the portrait is a `<button>` showing "Change" on hover and focus; clicking opens a menu with "Change photo" and, when `avatarUrl` is set, "Remove photo". No photo: "Add photo".
  - `ProfileComponent` passes `[editable]="isSelf()"`, opens a hidden `<input type="file" accept="image/jpeg,image/png,image/webp">`, shows the crop dialog, uploads, calls `auth.applyProfileUpdate(user)`, reloads the profile. Errors use `apiError(err, 'Could not save your photo.')` / `'Could not remove your photo.'`.

- [ ] **Step 1: Write failing specs:**
  - `users.service`: upload sends `PUT .../users/me/avatar` with `FormData` containing `file`; remove sends `DELETE`.
  - `id-badge`: not editable → no button and no hover label; editable without photo → menu offers "Add photo" only; editable with photo → "Change photo" and "Remove photo".
  - `profile`: own profile renders the badge editable, someone else's doesn't; a successful upload calls `applyProfileUpdate` with the returned user; "Save photo" is disabled while an upload is in flight and a second click sends nothing (Review Focus 5).
- [ ] **Step 2: Run** `npx ng test --watch=false` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full frontend suite → PASS; `npx ng build` → succeeds (CSS budget warnings only for pre-existing files).
- [ ] **Step 5: Browser check** with the local stack (S3Mock up): add → crop → photo appears on the badge (square) and topbar (round); replace; remove → monogram; another account sees the photo, without controls; HEIC/unsupported message; sideways phone JPEG upright.
- [ ] **Step 6: Commit** `feat: add, change and remove your photo from your badge (#63)`.

---

### Task 6: AWS bucket, CloudFront origin and deploy wiring

**Files:**
- Modify: `infra/MarqueeStack.cs` (bucket, behaviour, grants, output)
- Modify: `.github/workflows/deploy.yml`, `deploy.sh` (writes the host `.env`), `docker-compose.prod.yml`, `.env.prod.example`
- Modify: `docs/deployment/runbook.md` (new config value), `docs/deployment/verification-log.md` (new unticked live-check items)

**Interfaces:**
- Produces:
  - Bucket `Avatars` (settings from Global Constraints; `RemovalPolicy.RETAIN`).
  - Behaviour `["/avatars/*"]`: `S3BucketOrigin.WithOriginAccessControl(avatars)`, `REDIRECT_TO_HTTPS`, a new `CachePolicy` with `MaxTtl = Duration.Hours(24)`, `DefaultTtl = Duration.Hours(24)`, `MinTtl = Duration.Seconds(0)`, no cookies/headers/query strings in the key.
  - Host role: `s3:PutObject`, `s3:DeleteObject` on `avatars.ArnForObjects("avatars/*")` only.
  - Output `AvatarsBucketName`; `deploy.yml` reads it like the existing stack outputs and passes it through to `.env` as `AVATARS_BUCKET_NAME`; `docker-compose.prod.yml` maps `Avatars__BucketName=${AVATARS_BUCKET_NAME:?}` and `Avatars__PublicBaseUrl=/avatars`.

- [ ] **Step 1: Implement** the CDK changes.
- [ ] **Step 2: Verify** `cdk synth` succeeds, then `cdk diff MarqueeStack` (with `eval "$(aws configure export-credentials --format env)"` and `-c alertEmail=jdavidip2022@gmail.com` in the same command). Expected: new bucket, policy, cache policy, distribution **modify**, role policy update; **no** replacement of the instance, volume, EIP or distribution. Show the diff to the user.
- [ ] **Step 3: Validate the deploy wiring:** `bash -n` on the deploy script; render `docker compose -f docker-compose.prod.yml config` with a sample `.env` → both `Avatars__*` values present; missing `AVATARS_BUCKET_NAME` fails the render.
- [ ] **Step 4: Docs:** runbook lists the new value; verification log gains unticked items: upload, replace (old object gone from the bucket), remove, direct bucket URL refused (`403`), photo served from `/avatars/` with `Cache-Control: public, max-age=86400`.
- [ ] **Step 5: Commit** `feat: avatars bucket behind CloudFront (#63)`.
- [ ] **Step 6 (user, after #128 merged and this PR reviewed):** the user deploys `MarqueeStack` (`! ... cdk deploy MarqueeStack --require-approval never`), then merges (host running). Live check, then tick the verification-log items in a docs commit.

---

## Execution notes

- Commits are confirmed with the user one at a time; each task stops for review before the next.
- `marquee-reviewer` runs on the whole branch before the PR; only a clean "ready" opens it.
- PR body: `Closes #63`. Merge waits for #128 (Global Constraints).
