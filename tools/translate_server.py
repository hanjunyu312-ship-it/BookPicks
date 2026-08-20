#!/usr/bin/env python3
"""BookPicks local offline translator. JSON lines over stdin/stdout (UTF-8).

Request : {"id":7,"texts":["Atomic Habits","Fiction"]}   (also accepts "text":"..")
Response: {"id":7,"ok":true,"texts":["原子习惯","虚构"]}
          {"id":7,"ok":false,"error":"..."}
Events  : {"event":"ready","elapsed_s":42.3,"transformers":"5.15.1"}  once after model load
          {"event":"fatal","error":"..."}  then exit with non-zero code
Ping    : {"id":-1,"ping":true} -> {"id":-1,"pong":true}

Exit when stdin closes (C# kills the process tree on dispose).
"""
import argparse
import json
import os
import sys
import time
import traceback


def log(msg):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), msg), file=sys.stderr, flush=True)


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--beams", type=int, default=4)
    ap.add_argument("--max-tokens", type=int, default=512)
    ap.add_argument("--batch-size", type=int, default=32)
    args = ap.parse_args()

    # Windows 管道默认 cp936 —— 中文乱码的头号坑,必须显式 UTF-8
    sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    t0 = time.time()
    try:
        import torch
        from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
        from transformers import __version__ as tf_version
    except Exception as e:
        emit({"event": "fatal", "error": "missing deps: %s" % e})
        return 2
    try:
        tok = AutoTokenizer.from_pretrained(args.model)
        model = AutoModelForSeq2SeqLM.from_pretrained(args.model)
        model.eval()
    except Exception:
        emit({"event": "fatal", "error": traceback.format_exc()[-2000:]})
        return 3

    # 别占满所有核,给 WebView2/系统留 CPU
    torch.set_num_threads(max(2, (os.cpu_count() or 4) // 2))
    emit({"event": "ready", "elapsed_s": round(time.time() - t0, 1), "transformers": tf_version})
    log("ready in %.1fs" % (time.time() - t0))

    # opus-mt 位置嵌入上限 512:输入+输出总长不能超过,输出上限按剩余空间动态收缩
    model_max = getattr(model.config, "max_position_embeddings", 512)
    in_limit = min(args.max_tokens, model_max // 2)

    def translate(texts):
        out = []
        for i in range(0, len(texts), args.batch_size):
            batch = texts[i:i + args.batch_size]
            enc = tok(batch, return_tensors="pt", padding=True,
                      truncation=True, max_length=in_limit)
            in_len = enc["input_ids"].shape[1]
            out_limit = max(16, model_max - in_len)
            with torch.no_grad():
                gen = model.generate(
                    **enc, num_beams=args.beams,
                    max_new_tokens=min(args.max_tokens, out_limit))
            out.extend(tok.batch_decode(gen, skip_special_tokens=True))
        return out

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except Exception:
            continue
        rid = req.get("id")
        if req.get("ping"):
            emit({"id": rid, "pong": True})
            continue
        texts = req.get("texts")
        if texts is None and "text" in req:
            texts = [req["text"]]
        if not isinstance(texts, list) or not texts:
            emit({"id": rid, "ok": False, "error": "empty texts"})
            continue
        texts = [t if isinstance(t, str) else str(t) for t in texts]
        try:
            # 空串/纯空白进 padding batch 会让 generate 报错,过滤后按位置拼回
            idxs = [i for i, t in enumerate(texts) if t.strip()]
            results = list(texts)
            if idxs:
                zh = translate([texts[i] for i in idxs])
                for i, z in zip(idxs, zh):
                    results[i] = z
            emit({"id": rid, "ok": True, "texts": results})
        except Exception:
            log(traceback.format_exc())
            emit({"id": rid, "ok": False, "error": traceback.format_exc()[-2000:]})
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (BrokenPipeError, KeyboardInterrupt):
        sys.exit(0)
