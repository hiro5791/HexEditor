#!/usr/bin/env bash
# CI だけで動く (fs-crosscheck-images ジョブ)。実際の道具 (mke2fs・xorriso・mkudffs・lvm2・mkfs.hfsplus) でファイルシステムのイメージを作り、
# 元のファイルのマニフェスト (パス・サイズ・更新日時・SHA-256) を書く。Windows のジョブ (fs-crosscheck) が、アプリの解析で同じ一覧と内容になるかを
# 確かめる (FOR-07・FOR-12〜FOR-14)。開発者の PC では動かさない (ループデバイスのマウントに sudo を使う)。
set -euo pipefail

out="${1:?output folder}"
mkdir -p "$out"
src="$(mktemp -d)"
export LANG=C.UTF-8 LC_ALL=C.UTF-8
export E2FSPROGS_FAKE_TIME=1767225600
export SOURCE_DATE_EPOCH=1767225600

# ---- 元のファイル (決まった内容と日時) ----
python3 - "$src" <<'PY'
import hashlib, json, os, random, sys
src = sys.argv[1]
rnd = random.Random(0x5EED)
files = {
    "readme.txt": ("HexEditor file system cross check\n" * 128).encode(),
    "data.bin": rnd.randbytes(1 << 20),
    "big.bin": rnd.randbytes(5 << 20),
    "empty.txt": b"",
    "small.txt": b"tiny file\n",
    "nested/inner/deep.txt": b"deep file\n" * 10,
    "日本語の名前.txt": "日本語の内容\n".encode("utf-8") * 50,
    "boot.img": bytes(i & 0xFF for i in range(2048)),
}
for i in range(1, 301):
    files[f"many/f{i:03d}.txt"] = f"file-{i:03d}\n".encode()
t = 1767225600
for n, (path, data) in enumerate(sorted(files.items())):
    full = os.path.join(src, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "wb") as f:
        f.write(data)
    os.utime(full, (t + n * 61, t + n * 61))
PY

manifest() {
    python3 - "$1" "$2" <<'PY'
import hashlib, json, os, sys
root, target = sys.argv[1], sys.argv[2]
items = []
for d, _, names in os.walk(root):
    for name in names:
        full = os.path.join(d, name)
        rel = "/" + os.path.relpath(full, root).replace(os.sep, "/")
        st = os.stat(full)
        with open(full, "rb") as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        items.append({"path": rel, "size": st.st_size, "mtime": int(st.st_mtime), "sha256": digest})
items.sort(key=lambda x: x["path"])
with open(target, "w", encoding="utf-8") as f:
    json.dump(items, f, ensure_ascii=False, indent=1)
PY
}

manifest "$src" "$out/manifest.json"
dpkg-query -W e2fsprogs xorriso udftools lvm2 hfsprogs > "$out/tool-versions.txt" || true

# ---- ext2 / ext3 / ext4 (mke2fs -d) ----
mke2fs -q -t ext2 -b 4096 -U 00000000-0000-0000-0000-0000000000e2 -E root_owner=0:0 -d "$src" "$out/ext2.img" 64M
mke2fs -q -t ext3 -b 4096 -U 00000000-0000-0000-0000-0000000000e3 -E root_owner=0:0 -d "$src" "$out/ext3.img" 64M
mke2fs -q -t ext4 -b 4096 -O 64bit,flex_bg,extent,metadata_csum -U 00000000-0000-0000-0000-0000000000e4 -E root_owner=0:0 -d "$src" "$out/ext4.img" 64M

# ---- ISO 9660 (Joliet・Rock Ridge・El Torito) ----
xorriso -as mkisofs -quiet -input-charset utf-8 -J -R -V ISOTEST -b boot.img -no-emul-boot -boot-load-size 4 -o "$out/iso.iso" "$src"

# ---- UDF 2.01 (mkudffs で作り、マウントして書く。Linux は 2.50 のメタデータパーティションに書けないため、2.50 は生成ツールのイメージで確かめる) ----
mnt="$(mktemp -d)"
truncate -s 64M "$out/udf201.img"
mkudffs --media-type=hd --udfrev=0x0201 --label=UDFTEST "$out/udf201.img" > /dev/null
sudo mount -o loop -t udf "$out/udf201.img" "$mnt"
sudo cp -a "$src/." "$mnt/"
sudo umount "$mnt"

# ---- HFS+ (マウントできる環境だけ。リソースフォークも書く) ----
truncate -s 64M "$out/hfsplus.img"
mkfs.hfsplus -v HFSTEST "$out/hfsplus.img" > /dev/null
if sudo mount -o loop -t hfsplus "$out/hfsplus.img" "$mnt" 2> /dev/null; then
    sudo cp -a "$src/." "$mnt/"
    printf 'RSRC-FORK\n' | sudo tee "$mnt/readme.txt/..namedfork/rsrc" > /dev/null || true
    sudo umount "$mnt"
else
    echo "hfsplus cannot be mounted here; the HFS+ image is skipped" | tee "$out/hfsplus-skipped.txt"
    rm -f "$out/hfsplus.img"
fi

# ---- LVM2: 2 つの PV にまたがる linear の LV ----
truncate -s 32M "$out/pv0.img" "$out/pv1.img"
l0="$(sudo losetup --find --show "$out/pv0.img")"
l1="$(sudo losetup --find --show "$out/pv1.img")"
sudo pvcreate -q "$l0" "$l1"
sudo vgcreate -q vg0 "$l0" "$l1"
sudo lvcreate -q -l 100%FREE -n lv0 vg0 -y
size="$(sudo blockdev --getsize64 /dev/vg0/lv0)"
python3 -c "import random,sys; r=random.Random(0x1F); sys.stdout.buffer.write(r.randbytes($size))" > "$out/lv0.bin"
sudo dd if="$out/lv0.bin" of=/dev/vg0/lv0 bs=1M status=none conv=fsync
sudo vgchange -q -an vg0
sudo losetup -d "$l0" "$l1"
sha256sum "$out/lv0.bin" | cut -d' ' -f1 > "$out/lv0.sha256"
rm -f "$out/lv0.bin"
sudo chown -R "$(id -u):$(id -g)" "$out"
ls -l "$out"
