#!/bin/bash
# make-trailer.sh <dir with trailer-*.mp4> <out dir>
# Assembles the Microsoft Store trailer from the takes scenes/store-trailer.ps1 records:
# each 1600x900 take sits 1:1 (no scaling, so text stays sharp) on a 1920x1080 brand-navy
# canvas with a caption under it, takes are joined with short cross-fades, and an end card
# with the wordmark closes it. Writes pgnimbus-trailer.mp4 (H.264 + a silent AAC track)
# and pgnimbus-trailer-thumbnail.png, the 1920x1080 still Partner Center asks for.
set -euo pipefail
in="$1"; out="$2"
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
mkdir -p "$out"
cd "$out"

navy=0x242b36
bold="C\\:/Windows/Fonts/seguisb.ttf"
regular="C\\:/Windows/Fonts/segoeui.ttf"
fade=0.5

# take | start | length | caption
takes=(
  "trailer-1-start|1.2|2.8|Opens in under half a second"
  "trailer-2-palette|1.4|6.4|Ctrl+K jumps to any table or command"
  "trailer-3-completion|0.5|13.0|Autocomplete that writes the JOIN from your foreign keys"
  "trailer-4-safe-mode|0.6|22.6|Safe mode: review the SQL, commit every change as one transaction"
  "trailer-5-explain|0.8|16.8|EXPLAIN ANALYZE as a tree, heat-mapped by time, rows, cost or buffers"
)

inputs=(); filters=""; n=0; offset=0; prev=""; total=0
for t in "${takes[@]}"; do
  IFS='|' read -r name ss len cap <<<"$t"
  printf '%s' "$cap" > "cap$n.txt"
  inputs+=(-ss "$ss" -t "$len" -i "$in/$name.mp4")
  filters+="[$n:v]fps=30,format=yuv420p,setsar=1,pad=1920:1080:160:36:color=$navy,"
  filters+="drawbox=x=159:y=35:w=1602:h=902:color=white@0.10:t=1,"
  filters+="drawtext=fontfile='$bold':textfile=cap$n.txt:fontsize=40:fontcolor=white:x=(w-text_w)/2:y=1008-text_h/2[s$n];"
  n=$((n+1))
done

# End card: the wordmark and one line under it.
printf '%s' 'Free and open source. No account, no telemetry.' > capend.txt
inputs+=(-f lavfi -t 4 -i "color=c=$navy:s=1920x1080:r=30" -i "$repo/design/masters/logo/wordmark-dark.png")
filters+="[$((n+1)):v]scale=900:-1[wm];[$n:v][wm]overlay=(W-w)/2:360,format=yuv420p,setsar=1,"
filters+="drawtext=fontfile='$regular':textfile=capend.txt:fontsize=44:fontcolor=0xAAB2C0:x=(w-text_w)/2:y=700[s$n];"
lens=(); for t in "${takes[@]}"; do IFS='|' read -r _ _ len _ <<<"$t"; lens+=("$len"); done; lens+=(4)

# Cross-fade the chain: each xfade starts $fade before the running end.
prev="s0"; total=${lens[0]}
for ((i=1; i<=n; i++)); do
  off=$(awk "BEGIN{print $total-$fade}")
  filters+="[$prev][s$i]xfade=transition=fade:duration=$fade:offset=$off[x$i];"
  prev="x$i"; total=$(awk "BEGIN{print $total+${lens[$i]}-$fade}")
done
filters+="[$prev]fade=t=in:st=0:d=0.4:color=$navy[v]"

ffmpeg -loglevel error -y "${inputs[@]}" -f lavfi -t "$total" -i "anullsrc=r=48000:cl=stereo" \
  -filter_complex "$filters" -map "[v]" -map "$((n+2)):a" \
  -c:v libx264 -preset slow -crf 18 -profile:v high -pix_fmt yuv420p -r 30 \
  -c:a aac -b:a 128k -shortest -movflags +faststart pgnimbus-trailer.mp4

# Thumbnail: the plan tree from the last frame of its take, where the mouse is parked
# off it (the cut above ends before that), framed like the video.
ffmpeg -loglevel error -y -sseof -0.2 -i "$in/trailer-5-explain.mp4" -frames:v 1 -update 1 \
  -vf "pad=1920:1080:160:36:color=$navy,drawbox=x=159:y=35:w=1602:h=902:color=white@0.10:t=1,drawtext=fontfile='$bold':textfile=cap4.txt:fontsize=40:fontcolor=white:x=(w-text_w)/2:y=1008-text_h/2" \
  pgnimbus-trailer-thumbnail.png
rm -f cap*.txt
echo "pgnimbus-trailer.mp4 ($total s)"
