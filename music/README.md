# Music for highlight reels

Put a few **royalty-free** tracks here (`.mp3`, `.m4a`, `.aac`, `.ogg`, `.opus`, `.wav`, `.flac`).
Each reel picks one at random, trims it to the video length and fades it out. No files = silent reels.

ShaadiLens ships no music: you are responsible for the licence. Free sources that allow use in videos
include the YouTube Audio Library, Pixabay Music and Free Music Archive (check each track's licence).

The folder is mounted read-only into the `reel-worker` container (`./music:/data/music:ro`) and is ignored by git.
Admin → Settings → Reels lists the tracks it found.
