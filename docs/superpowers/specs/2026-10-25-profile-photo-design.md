# Profile photo upload — design (#63)

## Intent

`User.AvatarUrl` exists and every face-drawing path already renders it (topbar, profile badge,
Premiere crowd strip, library header, friend lists), falling back to a monogram. Nothing writes it.
Two reasons to build the writer now:

- **Product:** the profile badge is the one page framed as an ID card, and with only initials it
  looks bare.
- **AWS:** this is the next deliberate AWS learning piece. The deployment is an AWS showcase, so
  storage is S3, served through the existing CloudFront distribution.

## Goals

- A signed-in user can **add, change and remove** their photo from their own profile badge.
- They **choose the crop** before anything is uploaded.
- The stored value round-trips through `AvatarUrl`, so every existing consumer works unchanged.
- The image the API stores is one it produced itself: decoded, resized, re-encoded, metadata gone.

## Non-goals

- **Admin removal or moderation** of someone else's photo. Out of scope for now (user's call).
- Hiding photos on private profiles. **A photo is always public**, like a username, even when the
  profile is private (user's call).
- Presigned direct-to-S3 uploads, image variants per size, animated images, Gravatar-style
  services, or pasting an image URL.

## Anti-goals (would make this a failure even if it works)

- Serving a user's original file: EXIF/GPS from phone photos must never reach the bucket.
- A public bucket, or a URL that can change what it points to (stale faces from cache).
- Anonymous visitors being able to write anything to S3.

## Ordering constraint

**#128 lands first, as its own PR.** Code in the API container reaches the EC2 instance role's
credentials only because of an AMI-default metadata hop limit; #128 makes it explicit in the stack
and says it must be done before #63 ships any AWS call from the container.

*(Claude's call)* #128's "leave it as an operational check" outcome does **not** satisfy this: #63's
S3 code does not merge until the hop limit is stated in code. If #128's `cdk diff` shows that cannot
be done without replacing the host, stop and bring the decision back to the user rather than
proceeding on the AMI default.

## Decided: user experience

- **Where the controls live (option B from the mockups).** On your *own* badge only, hovering the
  portrait shows "Change"; clicking it opens a small menu: "Change photo", plus "Remove photo" when
  a photo exists. With no photo, the same affordance adds one. Visitors see a plain badge, no
  controls, no hover state. Must be keyboard reachable (the portrait is a button for the owner).
- **The crop step.** After picking a file, a dialog opens before anything is uploaded:
  - title "Place your photo";
  - the photo behind a **square** frame (outside dimmed), draggable to position, with a zoom slider;
  - a **dashed circle guide inside the square**, shown only in this dialog, marking what the small
    round renderings keep. It is never part of the saved image;
  - "Cancel" and "Save photo".
- **One stored image, square.** The badge portrait is a large square (full badge width, ~270px CSS)
  and shows the whole image. The small places (topbar, friend lists, crowd strip, library header)
  already render it round with CSS. No second crop, no second file.
- **Remove** returns the user to the monogram everywhere.
- **Replace** swaps the face everywhere; the old object is deleted, not left in the bucket.
- File picker accepts JPEG, PNG, WebP. Refusals get a clear sentence-case message (too large, not an
  image, unsupported type).

## Decided: storage and serving

- **Upload goes through the API.** The browser crops client-side and sends the cropped square as
  multipart form data. The API does not trust it: it decodes it with **ImageSharp** (new dependency;
  pure managed, nothing added to the Docker image; Six Labors split licence, free for this
  non-commercial open-source project), rejects anything that is not really JPEG/PNG/WebP, resizes
  to a fixed **640×640**, and re-encodes as **WebP**. Re-encoding drops EXIF/GPS.
- **Bucket:** a new private S3 bucket in CDK, `BlockPublicAccess.BLOCK_ALL`, S3-managed encryption,
  `EnforceSSL`, matching the existing `Site` bucket's settings.
- **Keys:** `avatars/{userId}/{random}.webp`. Every upload is a new key; a key never changes content.
- **Serving:** a second origin on the existing CloudFront distribution in `MarqueeStack`, behaviour
  `/avatars/*` → the new bucket via `S3BucketOrigin.WithOriginAccessControl` (same pattern as the
  site bucket). Because keys are immutable, a cached photo is never stale.
- **Edge cache capped at 24 hours (user's call).** Deleting an object removes it from the bucket at
  once, but CloudFront edges keep serving their copy until it expires. The cap bounds that: a
  removed or replaced photo stops being reachable at its old URL within a day. No invalidation call
  and no `cloudfront:CreateInvalidation` permission; the extra origin fetches are negligible.
- **Permissions:** the host instance role gets only `s3:PutObject` and `s3:DeleteObject` on the
  bucket's `avatars/*` prefix.
- **Local development and tests: Adobe S3Mock** (Apache 2.0; image `adobe/s3mock`, 5.2.3 current on
  2026-09-19), as a `docker-compose.yml` service and as a Testcontainer in the integration tests, so
  the same AWS SDK code runs locally, in CI and in production.
  - Rejected: LocalStack (since March 2026 even S3 needs an account + auth token), MinIO (free
    images discontinued), a disk-backed dev-only store (S3 code would only run in production).
    SeaweedFS is the fallback if S3Mock falls short.
  - IAM, the bucket policy and CloudFront/OAC are only exercised on real AWS, in the live check.

## Decided by Claude (the user can overrule; flagged for review)

- **Upload limits:** request body capped at a few MB (suggest 5 MB) *before* decoding; also cap the
  decoded pixel dimensions so a small file cannot decompress into a huge bitmap.
- **Rate limiting:** a new named policy in the existing rate limiter (`RateLimitingRegistration`),
  per user, applied to the upload endpoint.
- **Auth:** upload and remove require a signed-in account; anonymous sessions get 401.
- **What `AvatarUrl` stores:** the full public URL built at upload time from a configured base
  (`Avatars:PublicBaseUrl` or similar) plus the key. In production the base is the relative
  `/avatars`, so stored values survive a future custom domain; locally it is S3Mock's bucket URL.
  The old object's key on replace/remove is derived from the stored value by stripping the base.
  A stored value that does not start with the configured base is never deleted from S3 (only the
  row is cleared).
- **Production config:** the bucket name reaches the API the same way existing deploy-time config
  does (stack output → `deploy.yml` → host `.env` → compose environment).
- **Blocked users:** no special handling; `UserAccessMiddleware` already refuses them before any
  endpoint runs.
- **Ordering of replace:** write the new object, update the row, then delete the old object. A
  failed delete leaves an orphan (acceptable; log it), never a broken face.
- **Endpoints:** shape and route left to the builder, following `UsersController`'s existing
  `api/users/me` pattern; both must be idempotent per CLAUDE.md §7 (removing with no photo succeeds).

## Left to the builder

- Endpoint names and DTOs; the ImageSharp encoder settings (quality etc.).
- The crop component's implementation (hand-rolled canvas vs a small library — measure before adding
  a dependency), keyboard handling inside the dialog, and how zoom/drag map to the exported square.
- Exact CDK construct layout, cache policy choice for `/avatars/*`, and the S3Mock wiring
  (bucket creation at startup, ports, Testcontainers fixture).
- How the frontend refreshes `auth.user()` / the profile after a change so the topbar updates at once.
- Test split: unit tests for validation/resize rules, integration tests against S3Mock for
  upload/replace/remove, frontend specs for the controls and dialog.

## Done when

- A signed-in user can add, change and remove their photo from their own badge, choosing the crop
  with the circle guide; visitors see no controls.
- The photo shows in every existing face-drawing place, round in the small ones, square on the
  badge; removal restores the monogram everywhere.
- Stored objects are 640×640 WebP with no EXIF; non-images and oversize uploads are refused.
- The bucket is private; photos load through CloudFront `/avatars/*` with edge caching capped at
  24 hours; the host role has only the two actions on the prefix.
- #128 has stated the hop limit in code before the S3 code merges.
- Local dev and integration tests run against S3Mock.
- Live check on AWS recorded in `docs/deployment/verification-log.md`: upload, replace (old object
  gone from the bucket), remove, and a direct bucket URL refused.
