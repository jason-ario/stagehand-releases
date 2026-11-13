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

This repository holds release binaries only; the source lives in a private repo.

## Publishing a version (maintainers)
Run **Actions → Build and release → Run workflow** with a version like `v0.2.0`. It builds the private FilmStudio source for macOS and Windows and publishes the release here; re-running a version replaces its files.
