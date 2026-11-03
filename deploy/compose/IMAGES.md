# Pinned container images

All deployment and test images are pinned by immutable multi-architecture
manifest digests. The digest is the manifest-list/index digest, so Docker
selects the correct platform child (linux/amd64, linux/arm64, ...) instead of
forcing one architecture's image onto another platform. Resolved 2026-11-03.

| Image | Digest | Platforms | Used by | Why pinned |
| --- | --- | --- | --- | --- |
| `mcr.microsoft.com/dotnet/sdk:10.0` | `sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b` | linux/amd64, linux/arm64, linux/arm/v7 | service Dockerfiles, quick/core-runtime/release/security scripts, Testcontainers helpers | reproducible build/test SDK; prevents a floating `10.0` tag from changing compiler/test behaviour |
| `mcr.microsoft.com/dotnet/aspnet:10.0` | `sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee` | linux/amd64, linux/arm64, linux/arm/v7 | service Dockerfiles | reproducible runtime |
| `postgres:18.6` | `sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81` | linux/amd64, linux/arm64/v8, linux/arm/v7 | compose, Testcontainers | identical database engine for local, CI and release |
| `redis:8.10.2` | `sha256:97ec88159e6d7cfb5ddef9015386087d58a912de80112f06ebdfa78fa5989901` | linux/amd64, linux/arm64/v8 | compose, Testcontainers, HA/fault scripts | identical projection store |
| `rabbitmq:4.3.6-management` | `sha256:54cb013d76cd1fd6c0a0708f34062ad61c452bb6f016b434c6c33f1a765d2b58` | linux/amd64, linux/arm64/v8, linux/arm/v7, linux/ppc64le | compose, HA overlay, Testcontainers | identical broker/topology behaviour |
| `jaegertracing/all-in-one:1.76.0` | `sha256:ab6f1a1f0fb49ea08bcd19f6b84f6081d0d44b364b6de148e1798eb5816bacac` | multi-arch index | compose obs profile | diagnostics reproducibility |
| `otel/opentelemetry-collector-contrib:0.120.0` | `sha256:85ac41c2db88d0df9bd6145e608a3cb023f5d8443868adbfbbf66efb51087917` | multi-arch index | compose obs profile | diagnostics reproducibility |
| `prom/prometheus:v3.10.0` | `sha256:4a61322ac1103a0e3aea2a61ef1718422a48fa046441f299d71e660a3bc71ae9` | multi-arch index | compose obs profile | metrics gate reproducibility |
| `grafana/grafana:12.4.0` | `sha256:b0ae311af06228bcfd4a620504b653db80f5b91e94dc3dc2a5b7dab202bcde20` | linux/amd64, linux/arm64, linux/arm/v7 | compose obs profile | dashboard reproducibility |
| `alpine:3.21` | `sha256:ee16d92f96db08d9d2d07da48555a27f3a69d8f0759021dbc601195ed0d2b5bd` | multi-arch index | release-smoke teardown sentinel | deterministic test-only helper |

## Update procedure

1. Pick the new immutable tag and resolve its multi-arch digest:
   `docker buildx imagetools inspect <repository>:<tag>` (the `Digest:` line).
2. Update every occurrence (Dockerfiles, `compose.yaml`, HA overlay, test
   fixtures/scripts) in one change; never mix a floating tag with a digest.
3. Confirm the digest's platform list covers the deployment platforms; if a
   single-architecture child digest is intentionally needed, record the
   architecture and why.
4. Run the release smoke and exact-tree CI (including the full suite and the
   Production overlay) before merging.
5. Record old/new digests and the reason in the PR description.

The .NET SDK image pinned here is the 10.0 GA manifest index; `global.json`
still requires SDK `10.0.100` with `latestFeature`, which this image satisfies.
