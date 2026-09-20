# Release signing

ZeroSense never publishes an unsigned installer. Every tagged release includes
a portable Windows ZIP; `tools/build_release.ps1` only produces the optional
installer when an Authenticode certificate and password are supplied. The
private key must never be committed.

Required environment variables:

- `ZEROSENSE_SIGNING_CERTIFICATE`: path to a code-signing PFX.
- `ZEROSENSE_SIGNING_CERTIFICATE_PASSWORD`: PFX password.

The release build signs `zerosense.exe`, builds the per-user Inno Setup
installer, signs that installer with SHA-256 plus a trusted timestamp, verifies
the signature, and emits `SHA256SUMS.txt`. Local development can explicitly use
`-AllowUnsigned`; those outputs must not be published as official releases.

The GitHub release workflow expects the base64 PFX and its password in the
`WINDOWS_SIGNING_PFX_BASE64` and `WINDOWS_SIGNING_PFX_PASSWORD` repository
secrets. With neither secret configured, it publishes the portable ZIP and no
installer. Supplying only one secret fails the build. Rotate the certificate
immediately if either secret is exposed.
