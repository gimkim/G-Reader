# Reject malformed embedded ICC profiles per image

## Symptom

JPEG images downloaded as part of a Facebook album could appear washed out and
color-inverted in Full View even though the source file rendered normally in
other viewers. Other JPEG files were unaffected.

## Evidence

- The attached source matched
  `C:\Users\tatsa\OneDrive\camera\uploadedduck\10160412686344457.jpg` byte for
  byte (SHA-256
  `5AE4BEF88BD3C70AEC4E357C709552C9D4AD4F1FFE73A12E4EEDD6E81F3C3343`).
- Every JPEG currently in the folder contains a 456-byte `uRGB` ICC profile.
- 142 files share a structurally valid profile. The other 33 files share a
  variant differing by one byte: the `desc` tag declares 95 bytes instead of
  28, producing range `240..335` which partially overlaps `cprt` at `268..280`
  and subsequent tags.
- LittleCMS tolerates the malformed description and renders it like sRGB, while
  the Direct2D color-management effect produces the incorrect transform.

## Change

- Validate embedded ICC declared size, `acsp` signature, tag-table bounds,
  minimum tag payload size, and every tag data range before returning it to the
  renderer.
- Reject partially overlapping ranges while allowing multiple tags to share the
  exact same data range, as the valid Facebook profile does for
  `rTRC`/`gTRC`/`bTRC`.
- Bound tag count and use a sorted range comparison so corrupt metadata cannot
  turn validation into unbounded quadratic work.
- Trim harmless container padding after the ICC declared boundary.
- Return `null` only for the malformed image profile. Existing Full View and
  Thumbnail color-context behavior interprets that image as sRGB while monitor
  color management remains enabled for all valid profiles.
- Record the rejected page and structural reason when extended logging is
  enabled.

## Files

- `ColorProfileService.cs`
- `README.md`

## Validation

- `dotnet build .\CDisplayEx.CSharp.csproj -c Release --no-restore` passed with
  0 warnings and 0 errors after the final range-scan refinement.
- Reflection-based checks against the compiled validator used the real two
  Facebook ICC payloads:
  - valid 456-byte profile: accepted, including shared TRC data;
  - malformed profile: rejected because `cprt 268..280` overlaps
    `desc 240..335`;
  - synthetic out-of-bounds tag: rejected.
- A scan of all 175 JPEGs currently present accepted 142 and rejected exactly
  the 33 files carrying the overlapping-tag profile.
- Calling the compiled `ReadEmbeddedProfile` path against the reported image
  returned no custom profile (per-image sRGB fallback), while a valid-profile
  image from the same folder returned all 456 ICC bytes.

## Remaining manual UI test

- Rebuild and open one of the 33 affected Facebook JPEGs with monitor color
  management enabled. It should render as sRGB without changing color handling
  for one of the 142 valid-profile images.
