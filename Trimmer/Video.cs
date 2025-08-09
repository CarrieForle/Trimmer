using System.Diagnostics;
using System.Text;

namespace Trimmer.Trimmer
{
    using CodecInVideo = (int Index, string Codec);

    static class Video
    {
        // https://superuser.com/questions/554620/how-to-get-time-stamp-of-closest-keyframe-before-a-given-timestamp-with-ffmpeg
        // TODO: Handle cases where t >= video length.
        /// <summary>
        /// Find the timecode of first keyframe 
        /// after a timecode inclusively.
        /// </summary>
        /// <param name="timecode">The timecode.</param>
        /// <param name="src">The path of the video file.</param>
        /// <returns>
        /// A Task represents the timecode of the keyframe.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// No keyframe is found.
        /// </exception>
        internal static async Task<Timecode> FindKeyFrameAfter(Timecode timecode, string src)
        {
            string tmp_path = Path.GetTempFileName();

            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "error",
                    "-read_intervals", timecode.ToString(),
                    "-select_streams", "v",
                    "-show_entries", "packet=pts_time,flags",
                    "-of", "compact=nokey=1",
                    "-i", src,
                    "-o", tmp_path,
                },
            };

            // TODO: Probably not idiomatic? Need verification
            // whether this is thread-safe.
            Timecode? keyframe = await RunAndWaitAndThrowOnError(
                info: info,
                path: tmp_path,
                func: async (reader) =>
                {
                    while (await reader.ReadLineAsync() is not null and string line)
                    {
                        // Flag K is Keyframe
                        if (line.EndsWith("K__"))
                        {
                            double timestamp = double.Parse(line.Split("|")[1]);
                            keyframe = Timecode.OfSecond(timestamp);

                            if (keyframe >= timecode)
                            {
                                return keyframe;
                            }
                        }
                    }

                    return null;
                }
            );

            if (keyframe is null)
            {
                throw new InvalidDataException($"No keyframe is found after {timecode}.");
            }

            return keyframe;
        }

        internal static async Task<List<CodecInVideo>> GetVideoCodecs(string src)
        {
            var tmp_path = Path.GetTempFileName();

            var info = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = {
                    "-loglevel", "quiet",
                    "-select_streams", "v",
                    "-show_entries", "stream=index,codec_name,codec_type",
                    "-of", "compact=nokey=1",
                    "-i", src,
                    "-o", tmp_path,
                },
                RedirectStandardOutput = true,
            };

            var codecs = await RunAndWaitAndThrowOnError(
                info: info,
                path: tmp_path,
                func: async (reader) =>
                {
                    var codecs = new List<CodecInVideo>();

                    while (await reader.ReadLineAsync() is not null and string line)
                    {
                        var parts = line.Split("|");

                        if (parts[3] == "video")
                        {
                            var stream_index = int.Parse(parts[1]);
                            var codec = parts[2];
                            codecs.Add((stream_index, codec));
                        }
                    }

                    return codecs;
                }
            );

            return codecs;
        }

        // TODO: Ensure encoding quality
        // TODO: Does copy work for all audio codecs
        // so that it's not silent for few frames?
        // i.e., do we need to re-encode audio as well?
        /// <summary>
        /// Encode a video with FFmpeg.
        /// </summary>
        /// <param name="src">The path of the video file.</param>
        /// <param name="dst">
        /// The destination path of the 
        /// encoded video path .
        /// </param>
        /// <param name="codec_options">
        /// A string of chained -c option for FFmpeg commands.
        /// </param>
        /// <param name="from">The start timecode for encoding.</param>
        /// <param name="to">The end timecode for encoding.</param>
        /// <returns></returns>
        internal static async Task Encode(string src, string dst, IList<CodecInVideo> codecs, Timecode from, Timecode to)
        {
            var info = new ProcessStartInfo("ffmpeg");

            Array.ForEach([
                "-loglevel", "error",
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
                "-f", "mp4",
            ], info.ArgumentList.Add);

            foreach (var c in codecs) {
                info.ArgumentList.Add($"-c:{c.Index}");
                info.ArgumentList.Add(c.Codec);
            }

            Array.ForEach([
                "-map", "v?",
                "-map", "a?",
                "-map", "s?",
                dst,
            ], info.ArgumentList.Add);

            await RunAndWaitAndThrowOnError(info);
        }

        /// <summary>
        /// Remux a video with FFmpeg.
        /// The paremeters are the same as <c>Encode()</c>.
        /// </summary>
        internal static async Task Remux(string src, string dst, Timecode from, Timecode to)
        {
            var info = new ProcessStartInfo("ffmpeg");

            Array.ForEach([
                "-loglevel", "error",
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
                "-c", "copy",
                "-f", "mp4",
            ], info.ArgumentList.Add);

            Array.ForEach([
                "-map", "v?",
                "-map", "a?",
                "-map", "s?",
                dst,
            ], info.ArgumentList.Add);

            await RunAndWaitAndThrowOnError(info);
        }

        /// <summary>
        /// Concatenate 2 videos with FFmpeg.
        /// Videos on <c>path1</c> and <c>path2</c> must have
        /// the same streams with the same codecs.
        /// https://ffmpeg.org/ffmpeg-formats.html#concat
        /// https://trac.ffmpeg.org/wiki/Concatenate#samecodec
        /// </summary>
        /// <param name="path1">The path of the first video file.</param>
        /// <param name="path2">The path of the second video file.</param>
        /// <param name="dst">
        /// The destination 
        /// path of the concatenated video
        /// </param>
        /// <returns></returns>
        internal static async Task Merge(string path1, string path2, string dst)
        {
            var info_path = Path.GetTempFileName();

            try
            {
                using (var writer = File.CreateText(info_path))
                {
                    await writer.WriteAsync(
                    $"""
                    ffconcat version 1.0
                    file '{path1}'
                    file '{path2}'
                    """);
                }

                var info = new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList = {
                        // "-loglevel", "error",
                        "-y",
                        "-f", "concat",
                        "-safe", "0",
                        "-i", info_path,
                        "-c", "copy",
                        "-map", "v?",
                        "-map", "a?",
                        "-map", "s?",
                        dst,
                    },
                };

                await RunAndWaitAndThrowOnError(info);
            }
            finally
            {
                try
                {
                    File.Delete(info_path);
                }
                catch
                {

                }
            }
        }

        // `path` needs to be passed because it's up to the process
        // to decide where to write.
        /// <summary>
        /// <c>RundAndWaitAndThrowOnError</c> but you can
        /// do something with process's stdout.
        /// 
        /// It's recommended to pass temp file to <c>path</c>.
        /// See <c>Path.GetTempFileName()</c>.
        /// </summary>
        internal static async Task<T> RunAndWaitAndThrowOnError<T>(ProcessStartInfo info, Func<StreamReader, Task<T>> func, string path, bool delete = true)
        {
            await RunAndWaitAndThrowOnError(info);

            try
            {
                using (var reader = new StreamReader(path))
                {
                    return await func(reader).ConfigureAwait(false);
                }
            }
            finally
            {
                if (delete)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {

                    }
                }
            }
        }

        internal static async Task RunAndWaitAndThrowOnError(ProcessStartInfo info)
        {
            info.RedirectStandardError = true;
            info.UseShellExecute = false;

            using (var process = Process.Start(info)!)
            {
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string err = process.StandardError.ReadToEnd();
                    throw new InvalidDataException($"{info.FileName} failed:\n{err}");
                }
            }
        }
    }
}