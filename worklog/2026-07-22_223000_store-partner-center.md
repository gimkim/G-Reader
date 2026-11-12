# Worklog — 2026-07-22 22:30:00 ICT (historical reconstruction)

## Scope

Microsoft Partner Center Store listing questions from the chat. No runtime
source code was changed by these form-entry discussions.

## Guidance recorded

- Use an MSIX/PWA product submission, upload the generated `.msix` on the
  package page, and keep the reserved identity/publisher values unchanged.
- Select the PC, seated/standing experience; do not select HoloLens, game,
  camera, microphone, pen/ink, generative-AI, or purchase declarations unless
  the implementation actually uses them. Removable-drive, backup, and
  Windows capture declarations must match real behavior.
- Use the appropriate Books/Reference or utility/document-reader category;
  Photo & Video may be a secondary discovery category only if it accurately
  describes the product.
- Complete the IARC questionnaire as an “All Other App Types” utility/reader,
  not a game or social app; answer physical-media/rating-board questions
  accurately.
- Provide a truthful privacy policy URL/text. A local reader should not claim
  collection or transmission of personal data; Store-required declarations
  must be consistent with manifest capabilities.
- Supply Store logos, concise feature bullets, description, keywords, copyright,
  developer name, and release notes. The description should explain GPU
  acceleration, folders/archives/PDFs, layouts, caching, reading direction,
  history, and customizable controls.
- `runFullTrust` requires a concise justification explaining that the unpackaged
  desktop reader needs native GPU/PDF/archive codecs and local file access;
  remove the capability instead if the packaged architecture no longer needs
  it.

## Validation

This entry records form guidance from the conversation, not a claim that
Partner Center accepted every field or completed certification.
