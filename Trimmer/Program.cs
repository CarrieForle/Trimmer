using System.Globalization;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace Trimmer.Trimmer
{
    class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // Turkiye Test
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            var rootCommand = new RootCommand("Create clips from a video");

            var fromOpt = new Option<Timecode>("--from", ["-ss"])
            {
                Description = "The starting timecode of the video.",
                CustomParser = ParseTimecode,
                DefaultValueFactory = _ => Timecode.Zero(),
            };

            var toOpt = new Option<Timecode>("--to", ["-to"])
            {
                Description = "The ending timecode of the video.",
                CustomParser = ParseTimecode,
                DefaultValueFactory = _ => Timecode.End(),
            };

            var srcArg = new Argument<FileInfo>("src")
            {
                Description = "The path of the source video file.",
            };

            srcArg.AcceptExistingOnly();

            var outputArg = new Argument<string>("output")
            {
                Description = "The path of output video file.",
            };

            rootCommand.Options.Add(fromOpt);
            rootCommand.Options.Add(toOpt);
            rootCommand.Arguments.Add(srcArg);
            rootCommand.Arguments.Add(outputArg);

            ParseResult parseResult = rootCommand.Parse(args);

            foreach (ParseError parseError in parseResult.Errors)
            {
                Console.Error.WriteLine(parseError.Message);
            }

            rootCommand.SetAction(async (ParseResult) =>
            {
                var start = ParseResult.GetValue(fromOpt)!;
                var end = ParseResult.GetValue(toOpt)!;

                var src = ParseResult.GetValue(srcArg)!;
                var dst = ParseResult.GetValue(outputArg)!;

                var video = new Video(src.FullName);

                try
                {
                    await video.SmartTrim(start, end, dst).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                }
            });

            return await parseResult.InvokeAsync();
        }

        private static Timecode? ParseTimecode(ArgumentResult result)
        {
            if (result.Tokens.Count != 1)
            {
                result.AddError("Invalid syntax");
                return null;
            }
            try
            {
                return new Timecode(result.Tokens[0].Value);
            }
            catch (Exception e)
            {
                result.AddError(e.Message);
            }

            return null;
        }
    }
}