# Release synchronization

VictoryTool v1.3.1 includes portable avatar animation generation, CHR/EVENT selection and progress reporting, native clip hash ordering, auxiliary root bones, custom model companion resources, and custom shoes. Character body registration selects the skeleton owned by the selected animation profile; game playback was confirmed by the user after correcting that registration.

The private implementation and local public production mirror share the same runtime source and version. Preserve the production mirror layout: runtime and build scripts only, excluding private tests, dumps, generated characters and diagnostics. Public remote publication is explicitly prohibited for this task.

Validation: rebuild the public production mirror, compare runtime source byte-for-byte with the private repository, and verify the G4_Blender v2.0.0 installer. The user authorized synchronization of VictoryTool private with its remote. Public VictoryTool remains local only; its remote must not be pushed.

Completed: synchronized runtime sources verified byte-for-byte; public macOS ARM64 build succeeded; installer integrity, embedded v2.0.0 and matching ZIP aliases verified. Earlier local add-on version archives removed. User confirmed game playback before this release consolidation. Local release commits prepared; no remote push.

Distribution: v1.3.1 Windows x64 standalone EXE and macOS Apple Silicon APP rebuilt with existing scripts, copied to both local distribution directories. ZIP integrity, Windows executable signature, macOS bundle version and executable permissions checked; SHA256SUMS generated. No Windows runtime check or remote publication performed.

Synchronization: private release and distribution state prepared for origin/main; public production mirror remains synchronized locally without remote publication.
