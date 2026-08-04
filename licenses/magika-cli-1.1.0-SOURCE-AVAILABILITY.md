# Magika CLI 1.1.0 source availability

This distribution includes Magika CLI 1.1.0 for Windows x64 and code covered by the Mozilla Public License 2.0.

The exact MPL-covered source code shipped with the bundle is available at these bundle-relative paths:

- `sources/magika-cli-1.1.0/colored-3.0.0.crate` is the complete crates.io source archive for `colored` 3.0.0.
- `sources/magika-cli-1.1.0/eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip` is the exact Eigen source selected by ONNX Runtime 1.24.2. Its license files are included in the archive.

`licenses/magika-cli-1.1.0` contains the complete MPL-2.0 text for `colored`, Eigen's exact `COPYING.MPL2`, and ONNX Runtime's exact `ThirdPartyNotices.txt` including the Eigen notice. `THIRD-PARTY-NOTICES.txt` has a separate, checksum-linked section for every one of the 70 reviewed Magika runtime packages and preserves every license, notice, copying, and copyright file published in the 68 registry crate archives. The byte lengths and hashes are recorded in `magika-cli-1.1.0-redistribution.json` and checked by the release verifier.

The same directory also contains the exact Magika Cargo manifest and lock file, ONNX Runtime version and dependency-manifest extracts, Rust 1.94.1 standard-library attribution, and the reviewed 70-package Windows runtime inventory. Full permissively licensed upstream source archives are verified as release cache inputs but are not runtime dependencies and are not included in the executable bundle.

No changes were made to the bundled `colored` or Eigen source archives.
