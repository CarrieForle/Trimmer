# <img src="res/logo.ico" width="48"> Trimmer

Trimmer is a CLI tool to produce video clips with smart-encoding. This means minimizing re-encoding while doing streamcopy as much as possible. This allows to produce clips with minimal quality drop.

The functionality is backed by [FFmpeg and FFprobe](https://ffmpeg.org/). You must install and put them on PATH enviornment variable.

# How does it work

First, it finds the first keyframe from the starting point of the trim. 

Then re-encode the section from the starting point to the first keyframe, producing clip #1.

Then remux until the end of the trim, producing clip #2.

Note both clips contain only video streams.

It then concatenate the clips with [concat muxer](https://ffmpeg.org/ffmpeg-formats.html#concat-1), which allows to concatenate multiple videos with streamcopy. It also cuts and maps the audio streams from the original video.

# Build

Install .NET 9. Clone the repo and run:

```
dotnet run
```

