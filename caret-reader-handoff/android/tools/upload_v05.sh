#!/usr/bin/env bash
# 安卓 v0.5 上传到用户 bacloud 私密网盘（认证由代理注入）
BASE="https://drive-01.bacloud.online/public.php/dav/files/xEbRdH8jcDAbSAG"
enc() { python3 -c "import sys,urllib.parse;print('/'.join(urllib.parse.quote(x) for x in sys.argv[1].split('/')))" "$1"; }
mk() { curl -s -o /dev/null -w "MKCOL $1 %{http_code}\n" -X MKCOL -H "X-Requested-With: XMLHttpRequest" "$BASE/$(enc "$1")"; }
up() {
  for i in 1 2 3; do
    code=$(curl -s -o /dev/null -w "%{http_code}" -T "$1" -H "X-Requested-With: XMLHttpRequest" "$BASE/$(enc "$2")")
    echo "PUT $2 $code"; case $code in 200|201|204) return 0;; esac; sleep 10
  done; return 1
}
S=/workspace/storage/光标朗读软件/v0.5_Android
D="光标朗读软件/v0.5_Android"
mk "光标朗读软件"; mk "$D"
for f in 安装说明_安卓v0.5.txt 设计与功能对齐表_安卓v0.5.md 外部审查与辩论纪要_安卓v0.5.md SHA256.txt 光标朗读_安卓_轻量版_v0.5.apk 光标朗读_安卓_标准版_v0.5.apk 光标朗读_安卓_全音色版_v0.5.apk; do up "$S/$f" "$D/$f"; done
echo UPLOAD_DONE
