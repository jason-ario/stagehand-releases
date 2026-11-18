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

## HyperBox Exporter for Unity

Brings Unity scenes, characters and animations into HyperBox. In Unity, open **Window → Package Manager → + → Add package from git URL…** and add these two, in this order:

1. `https://github.com/KhronosGroup/UnityGLTF.git`
2. `https://github.com/jason-ario/stagehand-releases.git?path=/unity/com.hyperbox.exporter`

Then open **Tools → HyperBox → Export to HyperBox…**. Details are in the [package's README](unity/com.hyperbox.exporter/README.md).

This repository holds the release binaries and the Unity exporter; the rest of the source lives in a private repo.

## Publishing a version (maintainers)
Run **Actions → Build and release → Run workflow** with a version like `v0.2.0`. It builds the private FilmStudio source for macOS and Windows and publishes the release here; re-running a version replaces its files.

The Unity exporter in `unity/` is a copy from FilmStudio. **Build and release** refreshes it, and **Actions → Sync Unity exporter** does so without a release. Don't edit it here: the sync overwrites it.
