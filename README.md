# Important

This is working in progress. Don't expect it to work. I'm hoping this would work with ffmpeg but I'm kind of stuck. See [How does it work](#how-does-it-work) to have the overview of the process. Feel free to contribute ❤️.

## How does it work

The user chooses a starting point from the video to clip. It tries to find the first keyframe from that point. 

Then re-encode the section from starting point to the first keyframe, producing clip #1.

Then remux the remaining part of the video, producing clip #2.

You might have realized there is no ending point involved. Yes, currently you cannot set the ending point. This "feature" will become necessary after I can make it work. 

It then merges the clip with [concat muxer](https://ffmpeg.org/ffmpeg-formats.html#concat-1), which allows to concatenate multiple videos with streamcopy under certain *criteria*. This produces the final clip and the two intermediate clips are deleted.

Since both clips are produced from the same video, they have the same streams which meets the criteria. But unfortunately the final clip do not play on MPC-HC, despite the two intermeidate clips work well.

If you have an idea how to work this out, or reasons why it doesn't work, please let me know!

# Trimmer

Trimmer is a CLI tool to produce video clips with smart-encoding. This means minimizing re-encoding while doing streamcopy as much as possible. This allows to produce clips with minimal quality drop.

The functionality is backed by [FFmpeg and FFprobe](https://ffmpeg.org/). You must install and put them on PATH enviornment variable.

# Build

Install .NET 9. Clone the repo and run:

```bash
dotnet run
```