# HyperBox — Downloads

Official builds of HyperBox for macOS and Windows.

**[Download the latest release →](https://github.com/jason-ario/stagehand-releases/releases/latest)**

| Platform | File |
|---|---|
| macOS (Apple Silicon & Intel) | `HyperBox-macOS.zip` |
| Windows 10/11 (64-bit) | `HyperBox-Windows.zip` |

### First launch
- **macOS:** unzip, drag HyperBox to Applications. Until builds are notarized, right-click → **Open** the first time.
- **Windows:** unzip and run `HyperBox.exe`. If SmartScreen appears, choose **More info → Run anyway**.

Each release includes `SHA256SUMS.txt` to verify downloads.

## Unity exporter

Brings Unity scenes, characters and animations into HyperBox. In Unity, open **Window → Package Manager → + → Add package from git URL…** and paste:

```
https://github.com/jason-ario/stagehand-releases.git?path=/unity/com.hyperbox.exporter
```

To stay on one version, add a tag to the end, e.g. `#exporter-v0.4.0`. It also needs UnityGLTF (`https://github.com/KhronosGroup/UnityGLTF.git`); see [the package's README](unity/com.hyperbox.exporter/README.md).

This repository holds release binaries and the Unity exporter package only; the source lives in a private repo. The exporter here is a copy, refreshed by **Sync Unity exporter** (also part of **Build and release**).

## Publishing a version (maintainers)
Run **Actions → Build and release → Run workflow** with a version like `v0.2.0`. It builds the private FilmStudio source for macOS and Windows and publishes the release here; re-running a version replaces its files.
