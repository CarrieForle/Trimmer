using System.Globalization;

namespace Trimmer.Trimmer
{
    using static Video;
    
    class Program
    {
        private static async Task Main(string[] args)
        {
            // Turkiye Test
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            var start = new Timecode("00:01:00.32312");
            var src = Path.GetFullPath(@"src.mp4");
            var dst = Path.GetFullPath(@"dst.mp4");

            try
            {
                await SmartTrim(src, dst, start, Timecode.End());
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
            }
        }

        private static async Task SmartTrim(string src, string dst, Timecode start, Timecode end)
        {
            Console.Write("Finding keyframe...");

            var keyframe_task = FindSplitFrameAfter(start, src);
            var codecs_task = GetVideoEncoders(src);

            var splitFrame = await keyframe_task.ConfigureAwait(false);
            var codecs = await codecs_task.ConfigureAwait(false);

            Console.WriteLine("Done");

            var encode_dst = Path.GetTempFileName();
            var remux_dst = Path.GetTempFileName();

            try
            {
                Console.Write("Encoding and remuxing...");

                await Task.WhenAll([
                    EncodeVideo(src, encode_dst, codecs, start, splitFrame.EncodeFrame),
                    RemuxVideo(src, remux_dst, splitFrame.RemuxFrame, end),
                    ]).ConfigureAwait(false);

                Console.WriteLine("Done");
                Console.Write("Merging clips...");

                await Merge(encode_dst, remux_dst, src, start, end, dst);

                Console.WriteLine("Done");
                Console.WriteLine($"Your video is stored at {dst}");
            }
            finally
            {
                try
                {
                    File.Delete(encode_dst);
                    File.Delete(remux_dst);
                }
                catch
                {

                }
            }
        }
    }
}