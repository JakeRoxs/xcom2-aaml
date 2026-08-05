# Shipped Dependency License Inventory

**Generated:** 2026-07-19

## Methodology

This report summarizes CycloneDX 1.6 evidence generated from completed staged `win-x64` and `linux-x64` payloads. The generator reads every shipped first-party `.deps.json`, requires its active runtime target to match the staged RID, follows `runtime`, `runtimeTargets`, `native`, and `resources` assets, and verifies that each selected asset exists in the payload. NuGet assets are byte-compared with their restored package files; `.deps.json` package hashes are checked against locked restore metadata. Runtime packs and manifest-pinned Steamworks assets are classified separately.

The Linux closure merges `AAML.Avalonia`, `AAML.ProtonWrapper`, and `AAML.SteamProbe`. Components are deduplicated by case-insensitive name and version while retaining every `aaml:shipped-asset` and `aaml:source-deps` property. Every shipped DLL, executable, ELF app host, native library, and resource satellite must have exactly one owner. Missing, conflicting, multiply-attributed, or unattributed assets fail generation.

Only components with a physically staged asset enter the closure and third-party notices. Restored-only build, test, analyzer, source-generator, macOS, WebAssembly, and wrong-RID packages are excluded. The generated `sbom.cdx.json` and `THIRD-PARTY-NOTICES.txt` files in each artifact are the authoritative per-file inventory and license evidence.

## Verified Summary

| Measure | Windows | Linux |
| --- | ---: | ---: |
| CycloneDX components | 88 | 89 |
| NuGet/runtime-pack components | 78 | 77 |
| First-party components | 8 | 10 |
| Explicit Steamworks/Valve components | 2 | 2 |
| Staged files before checksums | 294 | 686 |
| Release-blocking license text gaps | 0 | 0 |

- Deduplicated cross-platform component name+version entries: 94
- Authoritative license catalog mappings: 35
- Runtime-pack mapping: `runtimepack.Microsoft.NETCore.App.Runtime.{win-x64,linux-x64}` 10.0.10, MIT
- Steamworks.NET source revision: `cde64110bff012829b59cc16fe2c4fc3a0371e8d`, MIT
- Valve Steamworks SDK native redistributables: 1.64, Steamworks SDK agreement

## Exclusion Regression

Fixture and real-publish validation confirm that restored-only entries do not enter notices or the SBOM. This includes `Avalonia.BuildServices`, `Microsoft.Reactive.Testing`, ReactiveUI source generators/analyzers, macOS native assets, WebAssembly native assets, and the opposite RID's native assets.

The catalog remains validated against the complete locked restore inventory so malformed, ambiguous, unsafe, or orphaned mappings still fail closed. During evidence generation, only mappings selected by the exact shipped closure contribute notice sections or authoritative text appendices.
