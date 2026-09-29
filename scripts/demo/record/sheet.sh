#!/bin/bash
# sheet.sh in.mp4 out.png [cols] [rows] : a contact sheet of evenly spaced frames, each stamped with its time.
in="$1"; out="$2"; cols="${3:-4}"; rows="${4:-3}"
dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$in")
n=$((cols*rows))
fps=$(python -c "print($n/$dur)" 2>/dev/null || awk "BEGIN{print $n/$dur}")
ffmpeg -loglevel error -y -i "$in" -vf "fps=$fps,scale=480:-1,drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{pts\:hms}':x=6:y=6:fontsize=16:fontcolor=yellow:box=1:boxcolor=black@0.6,tile=${cols}x${rows}" -frames:v 1 -update 1 "$out" && echo "$out ($dur s)"
