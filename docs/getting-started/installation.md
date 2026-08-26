# Installation

pgNimbus ships for Windows, macOS and Linux. Windows is the primary target and
the most polished. The macOS and Linux builds are early beta.

## Windows

### Microsoft Store, recommended

The Store package is signed by Microsoft and updates itself, so there are no
SmartScreen warnings and nothing to re-download by hand.

[Get pgNimbus on the Microsoft Store](https://apps.microsoft.com/detail/9N6SZT42XJ24)

### WinGet

```powershell
winget install pgNimbus --source msstore
```

This installs the same Store package, so it carries the same signature.

### MSI

Download `pgNimbus-<version>-win-x64.msi` from the
[releases page](https://github.com/Shman4ik/pgNimbus/releases). It is a per-user
installer: it writes to `%LocalAppData%` and needs no administrator rights.

!!! warning "The MSI is unsigned"

    pgNimbus is a free project with no revenue, so it does not buy a code
    signing certificate. SmartScreen will warn on first run; click
    **More info** then **Run anyway**. If that bothers you, use the Store or
    WinGet path above. The Store re-signs the package for free, which is why it
    is the recommended route. You can also
    [verify the download's provenance](#verifying-a-download) instead of
    trusting the warning either way.

## macOS

Apple Silicon only. Download `pgNimbus-<version>-macos-arm64.dmg` from the
[releases page](https://github.com/Shman4ik/pgNimbus/releases). Open the disk
image, drag pgNimbus onto the Applications folder beside it, then eject the
image. Running the app from the mounted image works, but it disappears the
moment you eject.

The build carries an ad-hoc signature instead of an Apple Developer ID one, so
macOS asks about it the first time you open it:

=== "macOS 14 and earlier"

    Right-click (or Control-click) pgNimbus in Applications, choose **Open**,
    then **Open** again in the dialog.

=== "macOS 15 Sequoia and later"

    Double-click pgNimbus and dismiss the warning. Open
    **System Settings → Privacy & Security**, scroll to the security section,
    and click **Open Anyway** next to the message about pgNimbus.

You do this once per installed version. Later launches open normally.

!!! warning "If macOS says the app is damaged"

    That dialog means the download carries no signature at all, which is what
    0.11.1 and earlier shipped. There is no **Open Anyway** path out of it.
    Either download a later build, or clear the quarantine flag by hand:

    ```bash
    xattr -dr com.apple.quarantine /Applications/pgNimbus.app
    ```

    The command works as a fallback on any version, including from Downloads if
    you have not moved the app yet.

!!! note "No Intel build"

    GitHub retired its last Intel macOS runner in December 2025, so there is no
    hosted way to build an `osx-x64` binary. Apple Silicon only for now.

## Linux

x64 and arm64 builds, in three formats, all on the
[releases page](https://github.com/Shman4ik/pgNimbus/releases).

=== "AppImage"

    Works on any distribution and installs nothing.

    ```bash
    chmod +x pgNimbus-<version>-linux-<arch>.AppImage
    ./pgNimbus-<version>-linux-<arch>.AppImage
    ```

=== "Debian / Ubuntu"

    ```bash
    sudo apt install ./pgNimbus-<version>-linux-<arch>.deb
    pgnimbus
    ```

    The package also adds pgNimbus to your application menu. Its dependencies
    are the X11 libraries Avalonia needs plus fontconfig; the rendering stack
    itself is bundled.

=== "tar.gz"

    ```bash
    tar xf pgNimbus-<version>-linux-<arch>.tar.gz
    cd pgNimbus-<version>
    ./PgNimbus.App
    ```

## Verifying a download

The direct downloads are unsigned, but every release asset carries
[signed build provenance](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations).
One command proves a file was built by this repository's release workflow from
the tagged commit, rather than tampered with or rehosted:

```bash
gh attestation verify pgNimbus-<version>-win-x64.msi --repo Shman4ik/pgNimbus
```

Each release also ships `SHA256SUMS.txt` and a CycloneDX SBOM
(`pgNimbus-<version>-sbom.cdx.json`) listing every bundled dependency.

## Where pgNimbus keeps its files

| What | Location |
| --- | --- |
| Connection profiles, saved queries, history, settings, workspace | `<appdata>/pgNimbus/` |
| Crash log | `<appdata>/pgNimbus/logs/pgnimbus.log` |
| Passwords | The OS credential store: DPAPI on Windows, a permission-restricted file elsewhere. Never the profile file. |

On Windows `<appdata>` is `%AppData%`. On macOS and Linux it follows the
platform's usual application-data location.

## Next

[Connect to a database](connecting.md)
