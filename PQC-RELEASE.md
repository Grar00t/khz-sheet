# Post-quantum release packaging

KHZ Sheet source remains normal Git source. Encrypting a repository after publication does not erase prior clones, forks, cached refs, or previously distributed objects, so this project does not claim that rewriting Git history makes already-public source private.

The optional release bundle uses a hybrid construction:

- ML-KEM-768 encapsulates a shared secret to the release recipient public key.
- HKDF-SHA256 derives a key-encryption key from that shared secret.
- AES-256-GCM encrypts the release archive and separately wraps its random content key.
- ML-DSA-65 signs the canonical release manifest.
- SHA-256 records plaintext and ciphertext identities.

This is post-quantum cryptography. It does not use a quantum computer and does not make AES itself a lattice algorithm.
## Key boundary

`release/pqc-public/` contains only public keys and fingerprints. Secret keys are generated outside the repository and must never be committed. Their local storage path is intentionally not part of the repository contract.

Required packaging tools are development-only: Python 3, `liboqs-python`/liboqs with ML-KEM and ML-DSA enabled, and PyCryptodome for AES-GCM/HKDF. The desktop application does not depend on them.

Generate keys once:

```powershell
python tools/pqc_release.py keygen --key-dir <local-key-dir> --public-dir release\pqc-public
```

Encrypt and sign a release archive with `tools/pqc_release.py encrypt`; decrypt with `tools/pqc_release.py decrypt`. Decryption refuses a bad ML-DSA signature, ciphertext hash mismatch, GCM authentication failure, or final plaintext hash mismatch.
