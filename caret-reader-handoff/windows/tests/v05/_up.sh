BASE="https://drive-01.bacloud.online/public.php/dav/files/xEbRdH8jcDAbSAG"
enc() { python3 -c "import sys,urllib.parse;print('/'.join(urllib.parse.quote(x) for x in sys.argv[1].split('/')))" "$1"; }
up() { code=$(curl -s -o /dev/null -w "%{http_code} %{time_total}s %{speed_upload}B/s" -T "$1" -H "X-Requested-With: XMLHttpRequest" "$BASE/$(enc "$2")"); echo "PUT $2 $code"; }
