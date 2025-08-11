using System.Diagnostics;

namespace Trimmer.Trimmer
{
    using StreamEncoder = (int Index, string Encoder);
    using SplitFrame = (Timecode EncodeFrame, Timecode RemuxFrame);

    static class Video
    {
        // https://superuser.com/questions/554620/how-to-get-time-stamp-of-closest-keyframe-before-a-given-timestamp-with-ffmpeg
        // TODO: Handle cases where t >= video length.
        // TODO: Make it work for multiple video streams.
        /// <summary>
        /// Find the timecode of first keyframe 
        /// after a timecode inclusively.
        /// </summary>
        /// <param name="from">The timecode.</param>
        /// <param name="src">The path of the video file.</param>
        /// <returns>
        /// A Task represents the timecode of the keyframe.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// No keyframe is found.
        /// </exception>
        internal static async Task<SplitFrame> FindSplitFrameAfter(Timecode from, string src)
        {
            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "info",
                    "-read_intervals", from.ToString(),
                    "-select_streams", "v",
                    "-show_entries", "packet=pts_time,flags",
                    "-of", "compact=nokey=1",
                    "-i", src,
                },
            };

            SplitFrame? frames = await RunAndWaitAndThrowOnError<SplitFrame?>(
                info: info,
                func: async (reader) =>
                {
                    var timecodeBeforeKeyframe = new Queue<string>();
                    Timecode? keyframe = null;
                    const int checkPacketRange = 10;

                    while (await reader.ReadLineAsync() is not null and string line)
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
                            double timestamp = double.Parse(parts[1]);
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
                        var timecode = Timecode.OfSecond(double.Parse(s!));

                        if (timecode < keyframe && timecode > max)
                        {
                            max = timecode;
                        }
                    }

                    for (int i = 0; i < checkPacketRange && await reader.ReadLineAsync() is not null and string line; i++)
                    {
                        var timestamp = line.Split('|')[1];
                        var timecode = Timecode.OfSecond(double.Parse(timestamp));

                        if (timecode < keyframe && timecode > max)
                        {
                            max = timecode;
                        }
                    }

                    return (max, keyframe);
                }
            );

            return frames ?? throw new InvalidDataException($"Unable to find a split point after {from}.");
        }

        internal static async Task<List<StreamEncoder>> GetVideoEncoders(string src)
        {
            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "info",
                    "-select_streams", "v",
                    "-show_entries", "stream=index,codec_type:stream_tags=encoder",
                    "-of", "compact=nokey=1",
                    "-i", src,
                },
            };

            var codecs = await RunAndWaitAndThrowOnError(
                info: info,
                func: async (reader) =>
                {
                    var encoders = new List<StreamEncoder>();

                    while (await reader.ReadLineAsync() is not null and string line)
                    {
                        // Sample Line for HEVC: 
                        // stream|0|video|Lavc61.19.101 libx265
                        var parts = line.Split("|");

                        if (parts[2] == "video")
                        {
                            var stream_index = int.Parse(parts[1]);
                            var encoder = parts[3][(parts[3].IndexOf(' ') + 1)..];
                            encoders.Add((stream_index, encoder));
                        }
                    }

                    return encoders;
                }
            );

            return codecs;
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
        internal static async Task EncodeVideo(string src, string dst, IList<StreamEncoder> encoders, Timecode from, Timecode to)
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
                "-i", src,
                "-f", await GetContainer(src),
            ], info.ArgumentList.Add);

            foreach (var c in encoders)
            {
                info.ArgumentList.Add($"-c:{c.Index}");
                info.ArgumentList.Add(c.Encoder);
            }

            Array.ForEach([
                "-map", "v",
                dst,
            ], info.ArgumentList.Add);

            await RunAndWaitAndThrowOnError(info);
        }

        /// <summary>
        /// Remux a video with FFmpeg.
        /// The paremeters are the same as <c>Encode()</c>.
        /// </summary>
        internal static async Task RemuxVideo(string src, string dst, Timecode from, Timecode to)
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
                "-i", src,
                "-f", await GetContainer(src),
                "-c", "copy",
                "-map", "v",
                dst,
            ], info.ArgumentList.Add);

            await RunAndWaitAndThrowOnError(info);
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
        internal static async Task Merge(string encodeVideoPath, string remuxVideoPath, string originalVideoPath, Timecode from, Timecode to, string dst)
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
                    """);
                }

                string outputContainer = await GetContainer(dst);

                var info = new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList = {
                        "-benchmark",
                        "-loglevel", "info",
                        "-y",
                        "-f", "concat",
                        "-safe", "0",
                        "-i", infoPath,
                        "-c", "copy",
                        "-map", "0:v",
                        "-f", outputContainer,
                        intermediatePath,
                    },
                };

                await RunAndWaitAndThrowOnError(info);

                info = new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList = {
                        "-benchmark",
                        "-loglevel", "info",
                        "-y",
                        "-i", intermediatePath,
                        "-ss", from.ToString(),
                        "-i", originalVideoPath,
                    },
                };

                if (to == Timecode.End())
                {
                    Array.ForEach([
                        "-c", "copy",
                        "-map", "0:v",
                        "-map", "1:a?",
                        "-map", "1:s?",
                        "-f", outputContainer,
                        dst,
                    ], info.ArgumentList.Add);
                }

                await RunAndWaitAndThrowOnError(info);
            }
            finally
            {
                try
                {
                    File.Delete(infoPath);
                    File.Delete(intermediatePath);
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
                var res = await func(process.StandardOutput);

                // Close the stream to prevent deadlock.
                // See https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.standardoutput?view=net-9.0#remarks
                process.StandardOutput.Close();

                await WaitAndThrowOnError(process);

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
                await WaitAndThrowOnError(process);
            }
        }

        private static void SetCaptureStreamInfo(ProcessStartInfo info)
        {
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
        }

        private static async Task WaitAndThrowOnError(Process process)
        {
            // StandardError must be read before waiting,
            // otherwise deadlock might occur.
            //
            // See https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.standardoutput?view=net-9.0#remarks
            string err = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"{process.ProcessName} failed:\n{err}");
            }
        }

        /// <summary>
        /// Get the container (format) of a video.
        /// It will try to deduce from filename, otherwise
        /// use ffprobe to get the first available format.
        /// </summary>
        /// <param name="src">The path of video file</param>
        /// <returns>The container of the video</returns>
        private static async Task<string> GetContainer(string src)
        {
            if (Path.HasExtension(src))
            {
                return Path.GetExtension(src)[1..];
            }

            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "quiet",
                    "-i", src,
                    "-show_entries", "stream=index,codec_type:stream_tags=encoder",
                    "-of compact=nokey=1",
                }
            };

            string format = await RunAndWaitAndThrowOnError(
                info: info,
                func: async (reader) =>
                {
                    var line = (await reader.ReadLineAsync())!;
                    return line[line.IndexOf('|')..line.IndexOf(',')];
                });

            return format;
        }
    }
}