using System.Text.RegularExpressions;

namespace Trimmer.Trimmer
{
    partial class Timecode
    {
        private readonly string timecode;
        private static readonly Timecode zero = new(0);
        private static readonly Timecode end = new(decimal.MaxValue);
        private readonly decimal second;

        private string GenerateTimecodeString()
        {
            checked
            {
                ulong hour = (ulong)(this.second / 3600);
                int min = (int)(this.second / 60 % 60);

                return $"{hour:D2}:{min:D2}:{this.second % 60:0.######}";
            }
        }

        private Timecode(decimal second)
        {
            this.second = second;
            this.timecode = GenerateTimecodeString();
        }

        public Timecode(string timecode)
        {
            var match = TimecodeRegex().Match(timecode);

            if (!match.Success)
            {
                throw new Exception("Invalid timecode.");
            }
            
            List<int> parts = [0, 0];

            for (int i = 0; i < 2; i++)
            {
                if (match.Groups[i + 1].Success)
                {
                    parts[i] = int.Parse(match.Groups[i + 1].ToString());
                }
            }

            int hour;
            int minute;
            decimal second;

            // If minute is present in timecode
            if (match.Groups[2].Success)
            {
                hour = parts[0];
                minute = parts[1];
            }
            else
            {
                hour = 0;
                minute = parts[0];
            }

            if (match.Groups[3].Success)
            {
                second = decimal.Parse(match.Groups[3].ToString());
            }
            else
            {
                second = 0;
            }

            checked
            {
                this.second = hour * 3600 + minute * 60 + second;
            }

            this.timecode = GenerateTimecodeString();
        }

        public static Timecode Zero()
        {
            return zero;
        }

        /// <summary>
        /// Represent end of video. This is useful
        /// for ffmpeg commands that don't trim end.
        /// 
        /// Calling <c>ToString()</c> on this throws
        /// an exception.
        /// </summary>
        public static Timecode End()
        {
            return end;
        }

        public static bool operator >(Timecode a, Timecode b)
        {
            return a.second > b.second;
        }

        public static bool operator <(Timecode a, Timecode b)
        {
            return a.second < b.second;
        }

        public static bool operator >=(Timecode a, Timecode b)
        {
            return a.second >= b.second;
        }

        public static bool operator <=(Timecode a, Timecode b)
        {
            return a.second <= b.second;
        }

        public override string ToString()
        {
            if (this == end)
            {
                // throw new InvalidOperationException("Cannot call this on End Timecode.");
                return "Timecode.End";
            }

            return this.timecode;
        }

        /// <summary> 
        /// Note: The returned Timecode might not 
        /// be accurate to second due to rounding 
        /// error. Try not to pass a value too big.
        /// 
        /// As a baseline, <c>second</c> must not be greater
        /// than <c>int.MaxValue</c>.
        /// </summary> 
        public static Timecode OfSecond(decimal second)
        {
            if (second < 0)
            {
                throw new ArgumentException("Second is negative.");
            }

            return new Timecode(second);
        }

        [GeneratedRegex(@"^(?:([0-9]{1,2}):)?(?:([0-5]?[0-9]):)?((?:[0-5]?[0-9])(?:\.[0-9]{1,6})?)$")]
        private static partial Regex TimecodeRegex();
    }
}