cd /workspace/work/tts/review/win_v06/r2
export CTX=ctx.txt MAXTOK=6500
p=$1; shift
python3 ../rev.py groq $p "$@" 2>&1 | tail -2
