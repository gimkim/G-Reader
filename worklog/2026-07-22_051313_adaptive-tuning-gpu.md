# Worklog — 2026-07-22 05:13:13 ICT

## Scope

Adaptive performance tuning, migration notes, worker limits, and first GPU
thumbnail-stability pass.

## Work recorded

- `8c8da28` added `ImagePipelineTuning`, automatic performance profiling and
  benchmarking, explanatory settings, WIC/nvJPEG tuning, and `MIGRATION.md`.
- `b89d739` hardened GPU thumbnail scheduling and PDFium worker startup after
  thumbnail crashes/hangs.
- Worker/codec limits became bounded and cancellable; settings were intended
  to explain effective limits when one value caps another.
- The later project rule that automatic global fast-preview workers default to
  about half the logical cores was carried forward from this tuning work.

## Validation

Build/static checks passed. The user was expected to stress-test the real GPU
and large PDF/archive workloads.
