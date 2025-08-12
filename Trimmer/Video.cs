using System.Diagnostics;

namespace Trimmer.Trimmer
{
    using StreamEncoder = (int Index, string Encoder);
    using SplitFrame = (Timecode EncodeFrame, Timecode RemuxFrame);

    class Video
    {
        private bool inited = false;
        private string container;
        private IReadOnlyList<StreamEncoder> encoders;
        private string path;

        #pragma warning disable CS8618
        public Video(string src)
        #pragma warning restore CS8618
        {
            this.path = src;
        }

        private async Task init()
        {
            if (this.inited)
            {
                return;
            }

            if (!Path.Exists(this.path))
            {
                throw new ArgumentException("File is not found.");
            }

            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "info",
                    "-select_streams", "v",
                    "-show_entries", "stream=index:stream_tags=encoder:format=filename,format_name",
                    "-of", "compact=nokey=1",
                    "-i", this.path,
                },
            };

            (this.container, this.encoders) = await RunAndWaitAndThrowOnError(
                info,
                async (reader) =>
                {
                    var encoders = new List<StreamEncoder>();
                    string? container = null;

                    while (await reader.ReadLineAsync().ConfigureAwait(false) is not null and string line)
                    {
                        var parts = line.Split('|');

                        switch (parts[0])
                        {
                            case "stream":
                                var index = int.Parse(parts[1]);
                                var encoder = parts[2][(parts[2].IndexOf(' ') + 1)..];
                                encoders.Add((index, encoder));

                                break;
                            default:
                                string ext = Path.GetExtension(parts[1]);
                                var availableContainers = parts[2].Split(',');

                                if (ext != string.Empty)
                                {
                                    var nodot = ext[1..];

                                    if (availableContainers.Contains(nodot))
                                    {
                                        container = nodot;
                                    }
                                    else
                                    {
                                        container = availableContainers[0];
                                    }
                                }
                                else
                                {
                                    container = availableContainers[0];
                                }

                                break;
                        }
                    }

                    // I'm not sure if this is possible but sure.
                    if (container is null)
                    {
                        throw new InvalidDataException("File does not contain any container.");
                    }

                    return (container, encoders.AsReadOnly());
                }).ConfigureAwait(false);

            this.inited = true;
        }

        public async Task SmartTrim(Timecode start, Timecode end, string dst)
        {
            if (start >= end)
            {
                throw new ArgumentException("The starting point must be before the ending.");
            }

            Console.Write("Finding keyframe...");

            var initializeTask = this.init();
            var splitFrameTask = this.FindSplitFrameAfter(start);

            await initializeTask.ConfigureAwait(false);
            var splitFrame = await splitFrameTask.ConfigureAwait(false);

            Console.WriteLine("Done");

            var encodeDst = Path.GetTempFileName();
            var remuxDst = Path.GetTempFileName();

            try
            {
                Console.Write("Encoding and remuxing...");

                await Task.WhenAll([
                    this.EncodeVideo(encodeDst, start, splitFrame.EncodeFrame),
                    this.RemuxVideo(remuxDst, splitFrame.RemuxFrame, end),
                    ]).ConfigureAwait(false);

                Console.WriteLine("Done");
                Console.Write("Merging clips...");

                await Merge(encodeDst, remuxDst, start, end, dst).ConfigureAwait(false);

                Console.WriteLine("Done");
                Console.WriteLine($"Your video is stored at {dst}");
            }
            finally
            {
                try
                {
                    File.Delete(encodeDst);
                    File.Delete(remuxDst);
                }
                catch
                {

                }
            }
        }

        // https://superuser.com/questions/554620/how-to-get-time-stamp-of-closest-keyframe-before-a-given-timestamp-with-ffmpeg
        // TODO: Handle cases where t >= video length.
        // TODO: Make it work for multiple video streams.
        /// <summary>
        /// Find the timecode of first keyframe 
        /// after a timecode inclusively.
        /// </summary>
        /// <param name="from">The timecode.</param>
        /// <returns>
        /// A Task represents the timecode of the keyframe.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// No keyframe is found.
        /// </exception>
        private async Task<SplitFrame> FindSplitFrameAfter(Timecode from)
        {
            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "info",
                    "-read_intervals", from.ToString(),
                    "-select_streams", "v",
                    "-show_entries", "packet=pts_time,flags",
                    "-of", "compact=nokey=1",
                    "-i", this.path,
                },
            };

            SplitFrame? frames = await RunAndWaitAndThrowOnError<SplitFrame?>(
                info: info,
                func: async (reader) =>
                {
                    var timecodeBeforeKeyframe = new Queue<string>();
                    Timecode? keyframe = null;
                    const int checkPacketRange = 10;

                    while (await reader.ReadLineAsync().ConfigureAwait(false) is not null and string line)
                    {
                        string[] parts = line.Split('|');
                        timecodeBeforeKeyframe.Enqueue(parts[1]);

                        if (timecodeBeforeKeyframe.Count > checkPacketRange)
                        {
                            timecodeBeforeKeyframe.Dequeue();
                        }

                        // Flag K is Keyframe
                        if (parts[2] == "K__")
                        {
                            decimal timestamp = decimal.Parse(parts[1]);
                            keyframe = Timecode.OfSecond(timestamp);

                            if (keyframe > from)
                            {
                                break;
                            }
                        }
                    }

                    if (keyframe is null)
                    {
                        return null;
                    }

                    Timecode max = Timecode.Zero();

                    while (timecodeBeforeKeyframe.TryDequeue(out string? s))
                    {
                        var timecode = Timecode.OfSecond(decimal.Parse(s!));

                        if (timecode < keyframe && timecode > max)
                        {
                            max = timecode;
                        }
                    }

                    for (int i = 0; i < checkPacketRange && await reader.ReadLineAsync().ConfigureAwait(false) is not null and string line; i++)
                    {
                        var timestamp = line.Split('|')[1];
                        var timecode = Timecode.OfSecond(decimal.Parse(timestamp));

                        if (timecode < keyframe && timecode > max)
                        {
                            max = timecode;
                        }
                    }

                    return (max, keyframe);
                }
            ).ConfigureAwait(false);

            return frames ?? throw new InvalidDataException($"Unable to find a split point after {from}.");
        }

        /// <summary>
        /// Encode a video with FFmpeg.
        /// </summary>
        /// <param name="src">The path of the video file.</param>
        /// <param name="dst">
        /// The destination path of the 
        /// encoded video path.
        /// </param>
        /// <param name="encoders">
        /// A list of encoders used in <c>src</c> video stream.
        /// </param>
        /// <param name="from">The start timecode for encoding.</param>
        /// <param name="to">The end timecode for encoding.</param>
        /// <returns></returns>
        private async Task EncodeVideo(string dst, Timecode from, Timecode to)
        {
            var info = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList = {
                    "-benchmark",
                    "-loglevel", "info",
                    "-y",
                    "-ss", from.ToString(),
                }
            };

            if (to != Timecode.End())
            {
                info.ArgumentList.Add("-to");
                info.ArgumentList.Add(to.ToString());
            }

            Array.ForEach([
                "-i", this.path,
                "-f", this.container,
            ], info.ArgumentList.Add);

            foreach (var c in this.encoders)
            {
                info.ArgumentList.Add($"-c:{c.Index}");
                info.ArgumentList.Add(c.Encoder);
            }

            Array.ForEach([
                "-map", "v",
                dst,
            ], info.ArgumentList.Add);

            await RunAndWaitAndThrowOnError(info).ConfigureAwait(false);
        }

        /// <summary>
        /// Remux a video with FFmpeg.
        /// The paremeters are the same as <c>Encode()</c>.
        /// </summary>
        private async Task RemuxVideo(string dst, Timecode from, Timecode to)
        {
            var info = new ProcessStartInfo("ffmpeg");

            Array.ForEach([
                "-benchmark",
                "-loglevel", "info",
                "-y",
                "-ss", from.ToString(),
            ], info.ArgumentList.Add);

            if (to != Timecode.End())
            {
                info.ArgumentList.Add("-to");
                info.ArgumentList.Add(to.ToString());
            }

            Array.ForEach([
                "-i", this.path,
                "-f", this.container,
                "-c", "copy",
                "-map", "v",
                dst,
            ], info.ArgumentList.Add);

            await RunAndWaitAndThrowOnError(info).ConfigureAwait(false);
        }

        /// <summary>
        /// Merge videos with FFmpeg
        /// https://ffmpeg.org/ffmpeg-formats.html#concat
        /// https://trac.ffmpeg.org/wiki/Concatenate#samecodec
        /// </summary>
        /// <param name="encodeVideoPath">
        /// The path of thhe encoded video file.<
        /// /param>
        /// <param name="remuxVideoPath">
        /// The path of the remuxed video file.
        /// </param>
        /// <param name="originalVideoPath">
        /// The path of the original video file.
        /// </param>
        /// <param name="dst">
        /// The destination path of the concatenated video
        /// </param>
        /// <returns></returns>
        private async Task Merge(string encodeVideoPath, string remuxVideoPath, Timecode from, Timecode to, string dst)
        {
            var infoPath = Path.GetTempFileName();
            var intermediatePath = Path.GetTempFileName();

            try
            {
                using (var writer = File.CreateText(infoPath))
                {
                    await writer.WriteAsync(
                    $"""
                    ffconcat version 1.0
                    file '{encodeVideoPath}'
                    file '{remuxVideoPath}'
                    """).ConfigureAwait(false);
                }

                var info = new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList = {
                        "-benchmark",
                        "-loglevel", "info",
                        "-y",
                        "-f", "concat",
                        "-safe", "0",
                        "-i", infoPath,
                        "-ss", from.ToString(),
                    },
                };

                if (to != Timecode.End())
                {
                    info.ArgumentList.Add("-to");
                    info.ArgumentList.Add(to.ToString());
                }

                Array.ForEach([
                    "-i", this.path,
                    "-c", "copy",
                    "-map", "0:v",
                    "-map", "1:a?",
                    "-map", "1:s?",
                    "-f", this.container,
                    dst,
                ], info.ArgumentList.Add);

                await RunAndWaitAndThrowOnError(info).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    File.Delete(infoPath);
                }
                catch
                {

                }
            }
        }

        /// <summary>
        /// <c>RunAndWaitAndThrowOnError(ProcessStartInfo)</c> 
        /// but you can do something with process's stdout.
        /// </summary>
        private static async Task<T> RunAndWaitAndThrowOnError<T>(ProcessStartInfo info, Func<StreamReader, Task<T>> func)
        {
            SetCaptureStreamInfo(info);

            using (var process = Process.Start(info)!)
            {
                var res = await func(process.StandardOutput).ConfigureAwait(false);

                // Close the stream to prevent deadlock.
                // See https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.standardoutput?view=net-9.0#remarks
                process.StandardOutput.Close();

                await WaitAndThrowOnError(process, info.FileName).ConfigureAwait(false);

                return res;
            }
        }

        /// <summary>
        /// Run a process and wait it to finish.
        /// If the exit code is non-zero, it will 
        /// throw a exception.
        /// </summary>
        private static async Task RunAndWaitAndThrowOnError(ProcessStartInfo info)
        {
            SetCaptureStreamInfo(info);

            using (var process = Process.Start(info)!)
            {
                await WaitAndThrowOnError(process, info.FileName).ConfigureAwait(false);
            }
        }

        private static void SetCaptureStreamInfo(ProcessStartInfo info)
        {
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
        }

        // Get process name from the argument rather than
        // Process.ProcessName because the process
        // may end before we access it, which will cause
        // an exception.
        private static async Task WaitAndThrowOnError(Process process, string processName)
        {
            // StandardError must be read before waiting,
            // otherwise deadlock might occur.
            //
            // See https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.standardoutput?view=net-9.0#remarks
            string err = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"{processName} failed:\n{err}");
            }
        }
    }
}