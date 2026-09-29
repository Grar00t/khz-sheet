#!/usr/bin/env python3
import argparse, base64, hashlib, json, os, sys
from pathlib import Path
import oqs
from Crypto.Cipher import AES
from Crypto.Hash import SHA256
from Crypto.Protocol.KDF import HKDF

KEM = "ML-KEM-768"
SIG = "ML-DSA-65"
CTX = b"KHZ-SHEET-PQC-RELEASE-v1"

def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")

def unb64(text: str) -> bytes:
    return base64.b64decode(text.encode("ascii"), validate=True)

def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def canonical(obj) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":")).encode("utf-8")

def save(path: Path, data: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)

def keygen(key_dir: Path, public_dir: Path):
    key_dir.mkdir(parents=True, exist_ok=True)
    public_dir.mkdir(parents=True, exist_ok=True)
    kem_sk = key_dir / "khz-sheet-mlkem768.sk"
    sig_sk = key_dir / "khz-sheet-mldsa65.sk"
    if kem_sk.exists() or sig_sk.exists():
        raise SystemExit("refusing to overwrite existing secret keys")
    with oqs.KeyEncapsulation(KEM) as kem:
        kem_pk = kem.generate_keypair()
        save(kem_sk, kem.export_secret_key())
    with oqs.Signature(SIG) as sig:
        sig_pk = sig.generate_keypair()
        save(sig_sk, sig.export_secret_key())
    save(public_dir / "khz-sheet-mlkem768.pk", kem_pk)
    save(public_dir / "khz-sheet-mldsa65.pk", sig_pk)
    receipt = {
        "format": "khz-pqc-keys-v1", "kem": KEM, "signature": SIG,
        "kem_public_sha256": sha256(kem_pk),
        "signature_public_sha256": sha256(sig_pk),
    }
    (public_dir / "KEYS.json").write_text(json.dumps(receipt, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(receipt))

def encrypt(source: Path, output: Path, kem_pk_path: Path, sig_sk_path: Path, sig_pk_path: Path):
    plaintext = source.read_bytes()
    kem_pk = kem_pk_path.read_bytes()
    sig_sk = sig_sk_path.read_bytes()
    sig_pk = sig_pk_path.read_bytes()
    with oqs.KeyEncapsulation(KEM) as kem:
        kem_ct, shared = kem.encap_secret(kem_pk)
    salt = os.urandom(32)
    kek = HKDF(shared, 32, salt, SHA256, context=CTX + b"-KEK")
    content_key = os.urandom(32)
    wrap_nonce = os.urandom(12)
    wrap = AES.new(kek, AES.MODE_GCM, nonce=wrap_nonce)
    wrapped_key, wrap_tag = wrap.encrypt_and_digest(content_key)
    payload_nonce = os.urandom(12)
    payload = AES.new(content_key, AES.MODE_GCM, nonce=payload_nonce)
    ciphertext, payload_tag = payload.encrypt_and_digest(plaintext)
    output.mkdir(parents=True, exist_ok=True)
    payload_path = output / (source.name + ".aes256gcm")
    save(payload_path, ciphertext)
    manifest = {
        "format": "khz-pqc-release-v1",
        "source_name": source.name,
        "plaintext_bytes": len(plaintext),
        "plaintext_sha256": sha256(plaintext),
        "ciphertext_sha256": sha256(ciphertext),
        "kem": KEM,
        "signature": SIG,
        "payload_cipher": "AES-256-GCM",
        "kdf": "HKDF-SHA256",
        "kem_ciphertext": b64(kem_ct),
        "kdf_salt": b64(salt),
        "wrapped_key": b64(wrapped_key),
        "wrap_nonce": b64(wrap_nonce),
        "wrap_tag": b64(wrap_tag),
        "payload_nonce": b64(payload_nonce),
        "payload_tag": b64(payload_tag),
        "kem_public_sha256": sha256(kem_pk),
        "signature_public_sha256": sha256(sig_pk),
    }
    manifest_bytes = canonical(manifest)
    with oqs.Signature(SIG, sig_sk) as signer:
        signature = signer.sign(manifest_bytes)
    (output / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    save(output / "manifest.mldsa65.sig", signature)
    save(output / "khz-sheet-mldsa65.pk", sig_pk)
    print(json.dumps({
        "payload": str(payload_path),
        "ciphertext_sha256": sha256(ciphertext),
        "plaintext_sha256": sha256(plaintext),
        "signature_bytes": len(signature),
    }))

def decrypt(bundle: Path, output: Path, kem_sk_path: Path):
    manifest_path = bundle / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest_bytes = canonical(manifest)
    sig_bytes = (bundle / "manifest.mldsa65.sig").read_bytes()
    sig_pk = (bundle / "khz-sheet-mldsa65.pk").read_bytes()
    with oqs.Signature(SIG) as verifier:
        if not verifier.verify(manifest_bytes, sig_bytes, sig_pk):
            raise SystemExit("ML-DSA signature verification failed")
    ciphertext_path = bundle / (manifest["source_name"] + ".aes256gcm")
    ciphertext = ciphertext_path.read_bytes()
    if sha256(ciphertext) != manifest["ciphertext_sha256"]:
        raise SystemExit("ciphertext SHA-256 mismatch")
    kem_sk = kem_sk_path.read_bytes()
    with oqs.KeyEncapsulation(KEM, kem_sk) as kem:
        shared = kem.decap_secret(unb64(manifest["kem_ciphertext"]))
    kek = HKDF(shared, 32, unb64(manifest["kdf_salt"]), SHA256, context=CTX + b"-KEK")
    wrap = AES.new(kek, AES.MODE_GCM, nonce=unb64(manifest["wrap_nonce"]))
    content_key = wrap.decrypt_and_verify(
        unb64(manifest["wrapped_key"]), unb64(manifest["wrap_tag"]))
    payload = AES.new(content_key, AES.MODE_GCM, nonce=unb64(manifest["payload_nonce"]))
    plaintext = payload.decrypt_and_verify(ciphertext, unb64(manifest["payload_tag"]))
    if sha256(plaintext) != manifest["plaintext_sha256"]:
        raise SystemExit("plaintext SHA-256 mismatch")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(plaintext)
    print(json.dumps({"output": str(output), "sha256": sha256(plaintext),
                      "signature_verified": True}))

def main():
    parser = argparse.ArgumentParser(description="KHZ Sheet PQC release packaging")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("keygen")
    p.add_argument("--key-dir", type=Path, required=True)
    p.add_argument("--public-dir", type=Path, required=True)
    p = sub.add_parser("encrypt")
    p.add_argument("source", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--kem-public", type=Path, required=True)
    p.add_argument("--sig-secret", type=Path, required=True)
    p.add_argument("--sig-public", type=Path, required=True)
    p = sub.add_parser("decrypt")
    p.add_argument("bundle", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--kem-secret", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "keygen":
        keygen(args.key_dir, args.public_dir)
    elif args.command == "encrypt":
        encrypt(args.source, args.output, args.kem_public,
                args.sig_secret, args.sig_public)
    else:
        decrypt(args.bundle, args.output, args.kem_secret)

if __name__ == "__main__":
    main()
