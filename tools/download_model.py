#!/usr/bin/env python3
"""Download the free Helsinki-NLP/opus-mt-en-zh translation model (~300MB).

Only weights + tokenizer files are fetched (allow_patterns), not the whole
repo (which contains tf/flax/onnx copies, >1.2GB). Idempotent: huggingface_hub
skips files that already exist and match. Progress bar goes to stderr, visible
in the installer window.

Usage:
  python download_model.py --out <dir> [--repo <hf-repo>] [--mirror]
"""
import argparse
import os

ALLOW = [
    "config.json",
    "generation_config.json",
    "pytorch_model.bin",
    "tokenizer_config.json",
    "source.spm",
    "target.spm",
    "vocab.json",
    "special_tokens_map.json",
    "tokenizer.json",
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--repo", default="Helsinki-NLP/opus-mt-en-zh")
    ap.add_argument("--mirror", action="store_true")
    a = ap.parse_args()

    if a.mirror:
        os.environ["HF_ENDPOINT"] = "https://hf-mirror.com"
        print("Using mirror: https://hf-mirror.com", flush=True)

    from huggingface_hub import snapshot_download

    os.makedirs(a.out, exist_ok=True)
    snapshot_download(repo_id=a.repo, local_dir=a.out, allow_patterns=ALLOW)
    print("Model download complete.", flush=True)


if __name__ == "__main__":
    main()
