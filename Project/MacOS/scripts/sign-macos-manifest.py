#!/usr/bin/env python3
"""Sign a macOS update manifest and print the signed envelope.

Only the envelope structure (`format`, `keyId`, `payload`, `signature`) and the signing bytes
(`Feige.Update.v1\\n<keyId>\\n` + payload) are shared with the Windows updater. The algorithm is
platform specific: macOS verifies a P-256 ECDSA (DER) or Ed25519 signature with CryptoKit.

Usage:
    # one-time: create the release key (keep the private key off the repository)
    ./scripts/sign-macos-manifest.py keygen --key-id feige-macos-release-2026 --out ~/.feige/macos-release.pem
    # print the public key to paste into Project/MacOS/Updates/UpdateSource.json ("publicKey")
    ./scripts/sign-macos-manifest.py public-key --key ~/.feige/macos-release.pem
    # sign a release payload (the JSON file without the envelope)
    ./scripts/sign-macos-manifest.py sign --key ~/.feige/macos-release.pem \\
        --key-id feige-macos-release-2026 --payload payload.json --out feige-macos-update.json

`cryptography` is required for key generation and signing (`pip3 install cryptography`).
The signing key must never be committed, uploaded, or placed under Release/.
"""

import argparse
import base64
import json
import os
import sys

SIGNING_PREFIX = b"Feige.Update.v1\n"


def signing_bytes(key_id: str, payload: bytes) -> bytes:
    return SIGNING_PREFIX + key_id.encode("utf-8") + b"\n" + payload


def load_private_key(path: str):
    try:
        from cryptography.hazmat.primitives.serialization import load_pem_private_key
    except ImportError:  # pragma: no cover - environment dependent
        sys.exit("需要 Python cryptography 包：pip3 install cryptography")
    with open(path, "rb") as handle:
        return load_pem_private_key(handle.read(), password=None)


def command_keygen(args) -> None:
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import ec

    if os.path.exists(args.out):
        sys.exit(f"拒绝覆盖已有私钥：{args.out}")
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    key = ec.generate_private_key(ec.SECP256R1())
    pem = key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.PKCS8,
        serialization.NoEncryption(),
    )
    descriptor = os.open(args.out, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as handle:
        handle.write(pem)
    public = key.public_key().public_bytes(
        serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint
    )
    print(base64.b64encode(public).decode("ascii"))


def command_public_key(args) -> None:
    from cryptography.hazmat.primitives import serialization

    public = load_private_key(args.key).public_key().public_bytes(
        serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint
    )
    print(base64.b64encode(public).decode("ascii"))


def command_sign(args) -> None:
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import ec

    key = load_private_key(args.key)
    if args.algorithm != "p256" or not isinstance(key, ec.EllipticCurvePrivateKey) or key.curve.name != "secp256r1":
        sys.exit("当前脚本只签名 P-256（secp256r1）清单；Ed25519 请改用对应工具。")
    with open(args.payload, "rb") as handle:
        payload = handle.read()
    if len(payload) > 512 * 1024:
        sys.exit("发布记录过大。")
    # Decode before signing so a malformed payload never reaches a published manifest.
    document = json.loads(payload)
    if document.get("schemaVersion") != 1:
        sys.exit("发布记录 schemaVersion 必须为 1。")
    signature = key.sign(signing_bytes(args.key_id, payload), ec.ECDSA(hashes.SHA256()))
    envelope = {
        "format": 1,
        "keyId": args.key_id,
        "payload": base64.b64encode(payload).decode("ascii"),
        "signature": base64.b64encode(signature).decode("ascii"),
    }
    releases = document.get("releases") or []
    if not releases or len(releases) > 64:
        sys.exit("发布记录必须包含 1–64 个条目。")
    for release in releases:
        if release.get("platform") != "macos" or release.get("installation") != "macos":
            sys.exit("发布记录只能包含 macOS 应用条目。")
        if not str(release.get("downloadUrl", "")).endswith(".zip"):
            sys.exit("macOS 下载地址必须是 .zip 应用归档。")
        if len(str(release.get("sha256", ""))) != 64:
            sys.exit("每个条目都必须提供 SHA-256。")
    data = json.dumps(envelope, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if args.out:
        with open(args.out, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        print(f"已写入签名清单：{args.out}")
    else:
        sys.stdout.write(data.decode("utf-8"))


def main() -> None:
    parser = argparse.ArgumentParser(description="签名 macOS 更新清单")
    sub = parser.add_subparsers(dest="command", required=True)

    keygen = sub.add_parser("keygen", help="创建 P-256 发布签名私钥")
    keygen.add_argument("--key-id", required=True)
    keygen.add_argument("--out", required=True)
    keygen.set_defaults(func=command_keygen)

    public = sub.add_parser("public-key", help="打印 x963 公钥（Base64）")
    public.add_argument("--key", required=True)
    public.set_defaults(func=command_public_key)

    sign = sub.add_parser("sign", help="签名发布记录")
    sign.add_argument("--key", required=True)
    sign.add_argument("--key-id", required=True)
    sign.add_argument("--algorithm", default="p256", choices=["p256"])
    sign.add_argument("--payload", required=True)
    sign.add_argument("--out")
    sign.set_defaults(func=command_sign)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
