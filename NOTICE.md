# NOTICE — DataB_OpenNest

**DataB_OpenNest** (`DataB.OpenNest.gha`) is a **DataB-modified private fork** of OpenNest.
It is **not** the official OpenNest package and is distributed only as a private dependency of
the DataB Toolkit. Do not redistribute it as OpenNest.

It is based on **OpenNest** by Petras Vestartas, used under the MIT License (see `LICENSE`).
The original copyright notice is retained:

> MIT License — Copyright (c) 2019-2026 Petras Vestartas

## Modifications by DataB

- Batch-nesting layer for large part counts (OpenNest2 Batch) with pluggable part distribution.
- Synchronous solve when embedded/headless (clusters, ScriptEditor "Create Project", Player, Compute).
- Wall-clock **Timeout** option on all solvers; unnested parts reported as an error.
- Distinct package + assembly identity (`DataB_OpenNest` / `DataB.OpenNest.gha`, plugin GUID
  `d8a17c42-9b3e-4f6a-8c21-5e0b9d4f7a30`) so it never collides with a public OpenNest install.

Upstream OpenNest: https://github.com/petrasvestartas/OpenNest
